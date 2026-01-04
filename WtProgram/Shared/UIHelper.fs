namespace Bemo
open System
open System.Drawing
open System.Windows.Forms
open Bemo.Win32
open Aga.Controls.Tree

type SmoothNodeTextBox() = 
    inherit NodeControls.NodeTextBox()
    override this.Draw(node, context) =
        context.Graphics.TextRenderingHint <- System.Drawing.Text.TextRenderingHint.ClearTypeGridFit
        base.Draw(node, context)

[<AllowNullLiteral>]
type INode =
    abstract member showSettings : bool

type IntEditor() =
    let control = 
        let control = NumericUpDown()
        control.Minimum <- decimal(1)
        control.Maximum <- decimal(1000)
        control.Margin <- Padding(0)
        control
    interface IPropEditor with
        member x.value 
            with get() = box(int(control.Value))
            and set(newValue) = control.Value <- decimal(unbox<int>(newValue))
        member x.control = control :> Control
        member x.changed = control.ValueChanged |> Event.map ignore

type TextEditor() =
    let control = 
        let control = TextBox()
        control
    interface IPropEditor with
        member x.value 
            with get() = box(control.Text)
            and set(newValue) = control.Text <- unbox<string>(newValue)
        member x.control = control :> Control
        member x.changed = control.TextChanged |> Event.map ignore

type BoolEditor() =
    let control = CheckBox()
    interface IPropEditor with
        member x.value
            with get() = box(control.Checked)
            and set(newValue) = control.Checked <- unbox<bool>(newValue)
        member x.control = control :> Control
        member x.changed = control.CheckedChanged |> Event.map ignore

type EnumEditor<'e when 'e :> Enum>() as this =
    let control = ComboBox()
    let mutable cachedValue = null
    do this.init()

    member this.init() =
        for tag in Enum.GetValues(typeof<'e>) do
            let tag = tag.cast<'e>()
            control.Items.Add(tag.ToString()).ignore
        control.SelectedValueChanged.Add <| fun _ ->
            cachedValue <- control.SelectedItem

    member this.value
        with get() = 
            let value = cachedValue
            Enum.Parse(typeof<'e>, string(value)).cast<'e>()
        and set(value) =
            control.SelectedItem <- value.ToString()

    interface IPropEditor with
        member x.value
            with get() = this.value.cast<obj>()
            and set(value) = this.value <- value.cast<'e>()
        member x.control = control :> Control
        member x.changed = control.SelectedValueChanged |> Event.map ignore

type ColorEditor() as this =
    let changedEvent = Event<_>()
    let chooserButton = 
        let btn = Button()
        btn.Width <- btn.Height
        btn.Click.Add <| fun _ -> 
            let dlg = System.Windows.Forms.ColorDialog()
            dlg.Color <- this.color
            dlg.FullOpen <- true
            dlg.ShowHelp <- false
            if dlg.ShowDialog() = DialogResult.OK then
                (this :> IPropEditor).value <- dlg.Color
                changedEvent.Trigger()
        btn.Padding <- Padding(0)
        btn.Margin <- Padding(0)
        btn.Dock <- DockStyle.Right
        btn
        
    let textBox = 
        let tb = TextBox()
        let maxLen = 6
        let save() =
            (this :> IPropEditor).value <- this.colorFromTb
            changedEvent.Trigger()
        tb.Dock <- DockStyle.Fill
        tb.CharacterCasing <- CharacterCasing.Upper
        tb.Margin <- Padding(0)
        tb.KeyPress.Add <| fun e ->
            try
                if e.KeyChar = (char)Keys.Enter then
                    e.Handled <- true
                    save()
                elif Char.IsControl(e.KeyChar).not then
                    if tb.Text.Length + 1 - tb.SelectionLength  > maxLen then raise (Exception())
                    Int32.Parse(e.KeyChar.ToString(), Globalization.NumberStyles.HexNumber).ignore
            with ex -> 
                e.Handled <- true            
        tb.Validating.Add <| fun e ->
            try
                if tb.Text.Length > maxLen then raise (Exception())
                this.colorFromTb.ignore
            with ex -> 
                e.Cancel <- true
                tb.SelectAll()
                MessageBox.Show("Invalid color value, must be a six digit hexadecimal number.").ignore

        tb.Validated.Add <| fun e -> save()
        tb

    let panel = 
        let panel = TableLayoutPanel()
        panel.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        panel.RowCount <- 1
        panel.ColumnCount <- 2
        panel.RowStyles.Add(RowStyle(SizeType.Absolute, 25.0f)).ignore
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 0.9f)).ignore
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 25.0f)).ignore
        panel.AutoSize <- true
        panel.Padding <- Padding(0)
        panel.Margin <- Padding(0)
        panel.Controls.Add(textBox)
        panel.Controls.Add(chooserButton)
        panel.SetRow(textBox, 0)
        panel.SetColumn(textBox, 0)
        panel.SetRow(chooserButton, 0)
        panel.SetColumn(chooserButton, 1)
        panel
    member this.colorFromTb =
        let text = textBox.Text
        let value = Int32.Parse(text, Globalization.NumberStyles.HexNumber)
        Color.FromRGB(value)

    member this.color = chooserButton.BackColor
    interface IPropEditor with
        member x.value 
            with get() = 
                let text = textBox.Text
                let value = Int32.Parse(text, Globalization.NumberStyles.HexNumber)
                box(Color.FromRGB(value))
            and set(newColor) = 
                let color = unbox<Color>(newColor)
                chooserButton.BackColor <- color
                textBox.Text <- sprintf "%X" (color.ToRGB())
        member x.control = panel :> Control
        member x.changed = changedEvent.Publish


type HotKeyEditor() =
    let control = HotKeyControl()
    interface IPropEditor with
        member x.value 
            with get() = box(control.HotKey)
            and set(newValue) = control.HotKey <- unbox<int>(newValue)
        member x.control = control :> Control
        member x.changed = control.HotKeyChanged |> Event.map (fun _ -> ())

type HotKeyModifiersEditor() as this =
    let mutable _modifiers = Keys.None
    let modifiersChanged = Event<_>()
    let textBox = {  
        new TextBox() with
            override x.ProcessCmdKey(msg, keys) =
                this.modifiers <- Keys.Modifiers &&& keys
                true
    }
    do
        this.modifiers <- Keys.None
    
    member this.modifiers 
        with get() = _modifiers
        and set(newValue) = 
            textBox.Text <- newValue.ToString()
            _modifiers <- newValue
            modifiersChanged.Trigger()

    interface IPropEditor with    
        member this.value 
            with get() = box(this.modifiers)
            and set(value) = this.modifiers <- unbox<Keys>(value)
        member this.control = textBox :> Control
        member this.changed = modifiersChanged.Publish

type HotKeyOnlyEditor() as this =
    let mutable _hk = Keys.None
    let hkChanged = Event<_>()
    let textBox = {  
        new TextBox() with
            override x.ProcessCmdKey(msg, keys) =
                this.hk <- Keys.KeyCode &&& keys
                true
    }
    do
        this.hk <- Keys.None
    
    member this.hk 
        with get() = _hk
        and set(newValue) = 
            textBox.Text <- newValue.ToString()
            _hk <- newValue
            hkChanged.Trigger()

    interface IPropEditor with    
        member this.value 
            with get() = box(this.hk)
            and set(value) = this.hk <- unbox<Keys>(value)
        member this.control = textBox :> Control
        member this.changed = hkChanged.Publish

open System.Windows.Forms.VisualStyles

/// Color mode for DropdownButton focus highlighting
type DropdownButtonColorMode =
    /// ComboBox style: label highlights blue when focused AND menu is closed
    | ComboboxColor
    /// Button style: label never highlights, dropdown button shows hover state when focused
    | DropdownButtonColor

/// ComboBox-style dropdown button with label and dropdown icon
/// Supports keyboard navigation (Alt+Up/Down), focus highlighting, and toggle behavior
type DropdownButton(text: string, ?colorMode: DropdownButtonColorMode) =
    let colorMode = defaultArg colorMode ComboboxColor
    let changedEvent = Event<_>()

    // Container panel with border to look like ComboBox
    let container = new TableLayoutPanel()
    let textLabel = new Label()
    let dropdownBtn = new Button()
    let menu = new ContextMenuStrip()

    // Track mouse, focus and menu state for proper visual feedback
    let mutable isMouseOver = false
    let mutable isMouseDown = false
    let mutable isMenuOpen = false
    let mutable isFocused = false
    let mutable menuClosedTime = DateTime.MinValue

    do
        // Configure container
        container.RowCount <- 1
        container.ColumnCount <- 2
        container.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        container.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore
        container.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 17.0f)) |> ignore
        container.AutoSize <- true
        container.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        container.Margin <- Padding(0, 0, 0, 0)
        container.Padding <- Padding(0)
        container.Anchor <- AnchorStyles.Right
        container.BackColor <- SystemColors.Window
        container.BorderStyle <- BorderStyle.FixedSingle
        container.Cursor <- Cursors.Hand

        // Configure text label
        textLabel.Text <- text
        textLabel.AutoSize <- true
        textLabel.TextAlign <- ContentAlignment.MiddleLeft
        textLabel.Margin <- Padding(3, 3, 0, 3)
        textLabel.BackColor <- Color.Transparent
        textLabel.Cursor <- Cursors.Hand

        // Configure dropdown button
        dropdownBtn.Text <- ""
        dropdownBtn.Width <- 17
        dropdownBtn.Dock <- DockStyle.Fill
        dropdownBtn.FlatStyle <- FlatStyle.Flat
        dropdownBtn.FlatAppearance.BorderSize <- 0
        dropdownBtn.Margin <- Padding(0, 0, 0, 0)
        dropdownBtn.Cursor <- Cursors.Hand
        dropdownBtn.TabStop <- true

        // Helper to update label highlight state based on color mode
        let updateLabelHighlight () =
            match colorMode with
            | ComboboxColor ->
                // ComboBox style: highlight label when focused AND menu is closed
                if isFocused && not isMenuOpen then
                    textLabel.BackColor <- SystemColors.Highlight
                    textLabel.ForeColor <- SystemColors.HighlightText
                else
                    textLabel.BackColor <- Color.Transparent
                    textLabel.ForeColor <- SystemColors.ControlText
            | DropdownButtonColor ->
                // Button style: label never highlights
                textLabel.BackColor <- Color.Transparent
                textLabel.ForeColor <- SystemColors.ControlText
            // Invalidate dropdown button to update its visual state
            dropdownBtn.Invalidate()

        // Focus visual feedback
        dropdownBtn.GotFocus.Add <| fun _ ->
            isFocused <- true
            updateLabelHighlight()
        dropdownBtn.LostFocus.Add <| fun _ ->
            isFocused <- false
            updateLabelHighlight()

        // Mouse events
        dropdownBtn.MouseEnter.Add <| fun _ ->
            isMouseOver <- true
            dropdownBtn.Invalidate()
        dropdownBtn.MouseLeave.Add <| fun _ ->
            isMouseOver <- false
            if not isMenuOpen then isMouseDown <- false
            dropdownBtn.Invalidate()
        dropdownBtn.MouseDown.Add <| fun _ ->
            isMouseDown <- true
            dropdownBtn.Invalidate()
        dropdownBtn.MouseUp.Add <| fun _ ->
            if not isMenuOpen then isMouseDown <- false
            dropdownBtn.Invalidate()

        // Custom paint to draw ComboBox-style dropdown button
        dropdownBtn.Paint.Add <| fun e ->
            let state =
                if not dropdownBtn.Enabled then ComboBoxState.Disabled
                elif isMenuOpen || isMouseDown then ComboBoxState.Pressed
                elif isMouseOver then ComboBoxState.Hot
                elif colorMode = DropdownButtonColor && isFocused then ComboBoxState.Hot
                else ComboBoxState.Normal
            if ComboBoxRenderer.IsSupported then
                ComboBoxRenderer.DrawDropDownButton(e.Graphics, dropdownBtn.ClientRectangle, state)
            else
                ControlPaint.DrawComboButton(e.Graphics, dropdownBtn.ClientRectangle,
                    if isMenuOpen || isMouseDown then ButtonState.Pushed else ButtonState.Normal)

        // Menu keyboard events
        menu.PreviewKeyDown.Add <| fun e ->
            if e.KeyCode = Keys.Menu then
                e.IsInputKey <- true
            elif e.Alt && (e.KeyCode = Keys.Down || e.KeyCode = Keys.Up) then
                e.IsInputKey <- true

        menu.KeyDown.Add <| fun e ->
            if e.Alt && (e.KeyCode = Keys.Down || e.KeyCode = Keys.Up) then
                e.Handled <- true
                e.SuppressKeyPress <- true
                menu.Close()

        // Menu state tracking
        menu.Opened.Add <| fun _ ->
            isMenuOpen <- true
            updateLabelHighlight()
            dropdownBtn.Invalidate()
        menu.Closed.Add <| fun _ ->
            isMenuOpen <- false
            isMouseDown <- false
            menuClosedTime <- DateTime.Now
            updateLabelHighlight()
            dropdownBtn.Invalidate()

        // Toggle menu handler
        let handleMouseDown () =
            if isMenuOpen then
                menu.Close()
            else
                let elapsed = (DateTime.Now - menuClosedTime).TotalMilliseconds
                if elapsed > 200.0 then
                    menu.Show(container, Point(0, container.Height))

        // Click handlers
        // Focus the dropdown button when label or container is clicked (like ComboBox)
        container.MouseDown.Add <| fun _ ->
            dropdownBtn.Focus() |> ignore
            handleMouseDown()
        textLabel.MouseDown.Add <| fun _ ->
            dropdownBtn.Focus() |> ignore
            handleMouseDown()
        dropdownBtn.MouseDown.Add <| fun _ -> handleMouseDown()

        // Keyboard handler
        dropdownBtn.KeyDown.Add <| fun e ->
            if e.Alt && (e.KeyCode = Keys.Down || e.KeyCode = Keys.Up) then
                e.Handled <- true
                e.SuppressKeyPress <- true
                if isMenuOpen then
                    menu.Close()
                else
                    menu.Show(container, Point(0, container.Height))

        // Add controls to container
        container.Controls.Add(textLabel)
        container.SetRow(textLabel, 0)
        container.SetColumn(textLabel, 0)
        container.Controls.Add(dropdownBtn)
        container.SetRow(dropdownBtn, 0)
        container.SetColumn(dropdownBtn, 1)

    /// Add a menu item with click handler
    member this.AddItem(itemText: string, handler: unit -> unit) =
        let item = new ToolStripMenuItem(itemText)
        item.Click.Add <| fun _ -> handler()
        menu.Items.Add(item) |> ignore
        item

    /// Add a separator to the menu
    member this.AddSeparator() =
        menu.Items.Add(new ToolStripSeparator()) |> ignore

    /// Get the container control (for adding to parent)
    member this.Control = container :> Control

    /// Get the ContextMenuStrip (for advanced customization)
    member this.Menu = menu

    /// Get/Set the button text
    member this.Text
        with get() = textLabel.Text
        and set(value) = textLabel.Text <- value

    /// Check if menu is currently open
    member this.IsMenuOpen = isMenuOpen

module UIHelper =
    open System.Resources
    open System.Reflection

    let label text =
        let label = Label()
        label.AutoSize <- true
        label.Text <- text
        label.TextAlign <- ContentAlignment.MiddleLeft
        label
        
    

    let form (fields:List2<_>) =
        let panel = 
            let t = TableLayoutPanel()
            t.AutoScroll <- true
            t.AutoSize <- true
            t.Dock <- DockStyle.Fill
            //t.Padding <- Padding(10)
            t.RowCount <- fields.length
            t.ColumnCount <- 2
            // Add column width settings to match AppearanceView
            t.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 250.0f)).ignore
            t.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)).ignore
            // Add row height settings to match AppearanceView
            List2([0..fields.length-1]).iter <| fun row ->
                t.RowStyles.Add(RowStyle(SizeType.Absolute, 35.0f)).ignore
            t

        fields.enumerate.iter <| fun (i,(text, control:Control)) ->
            let caption = Localization.getString text
            let label = label caption
            control.Dock <- DockStyle.Fill
            label.Margin <- Padding(0,5,0,5)
            panel.Controls.Add(label)
            panel.Controls.Add(control)
            panel.SetRow(label, i)
            panel.SetColumn(label, 0)
            panel.SetRow(control, i)
            panel.SetColumn(control, 1)
        panel
              
    let vbox (controls:List2<Control>) =
        let t = 
            let t = TableLayoutPanel()
            t.AutoScroll <- true
            t.AutoSize <- true
            t.RowCount <- controls.length
            t.ColumnCount <- 1
            t
        controls.enumerate.iter <| fun(i,control) ->
            t.Controls.Add(control)
            t.SetRow(control, i)
            t.SetColumn(control, 0)
            t.RowStyles.Add(RowStyle()).ignore
        t  

    let hbox (controls:List2<Control>) =
        let t = 
            let t = TableLayoutPanel()
            t.AutoScroll <- true
            t.AutoSize <- true
            t.RowCount <- 1
            t.ColumnCount <- controls.length
            t
        controls.enumerate.iter <| fun(i,control) ->
            t.Controls.Add(control)
            t.SetRow(control, 0)
            t.SetColumn(control, i)
            t.ColumnStyles.Add(ColumnStyle()).ignore
        t  

    let okCancelForm control =
        let form = Form()
        form.Padding <- Padding(12)
        
        let okButton = Button()
        okButton.Text <- "OK"
        okButton.Click.Add <| fun _ ->
            form.DialogResult <- DialogResult.OK

        let cancelButton = Button()
        cancelButton.Text <- "Cancel"
        
        cancelButton.Click.Add <| fun _ ->
            form.DialogResult <- DialogResult.Cancel

        let buttonPanel = hbox (List2([okButton.cast<Control>(); cancelButton.cast<Control>()]))
        let vboxLayout = vbox (List2([control; buttonPanel.cast<Control>()]))
        vboxLayout.RowStyles.Item(0).SizeType <- SizeType.AutoSize
        vboxLayout.RowStyles.Item(1).SizeType <- SizeType.Absolute
        buttonPanel.Anchor <- AnchorStyles.Bottom ||| AnchorStyles.Right
        vboxLayout.Dock <- DockStyle.Fill
        form.Controls.Add(vboxLayout)
        form