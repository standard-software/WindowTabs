namespace Bemo
open System
open System.Drawing
open System.Windows.Forms
open Newtonsoft.Json.Linq

/// What the edge panels of the Appearance tab are built from. The top
/// edge's panel (AppearanceView) and the other edges' (EdgeSettingsPanel)
/// lay their rows out as the rest of the dialog does: a 250-px caption
/// column, the input, the reset button, on the same row grid.
module EdgeRows =
    let marginPx = UIHelper.settingsRowMarginPx

    /// The caption height that makes a single-line row the grid's height
    /// once its margins are added (AppearanceView.rowMinimumHeight).
    let captionHeightPx = UIHelper.settingsRowHeightPx - 2 * marginPx

    /// The unified "Dark Mode" toggle, which the owner-drawn drop-downs
    /// paint by.
    let isDarkModeEnabled () = DarkMode.darkModeEnabled

    let caption (text: string) =
        let label = Label()
        label.AutoSize <- true
        label.Text <- text
        label.TextAlign <- ContentAlignment.MiddleLeft
        label.Anchor <- AnchorStyles.Left
        label.Margin <- Padding(0, marginPx, 0, marginPx)
        label.MinimumSize <- Size(0, captionHeightPx)
        label

    /// Caption, input and reset columns, one AutoSize row each: a caption
    /// that wraps makes its row taller instead of being cut off.
    let rowTable (rows: int) =
        let p = TableLayoutPanel()
        p.Dock <- DockStyle.Top
        p.AutoSize <- true
        p.Margin <- Padding(0)
        p.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        p.RowCount <- rows
        p.ColumnCount <- 3
        for _ in 1 .. rows do p.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        p.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, float32 UIHelper.settingsLabelWidthPx)) |> ignore  // Caption
        p.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore   // Input
        p.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore          // Reset button
        p

    let place (table: TableLayoutPanel) (control: Control) (column: int) (row: int) =
        table.Controls.Add(control)
        table.SetRow(control, row)
        table.SetColumn(control, column)

    let resetText (value: string) = sprintf "%s:%s" (Localization.getString("Reset")) value

    /// A reset button filling the reset column, as the int rows' buttons.
    let resetButton (text: string) (onClick: unit -> unit) =
        let btn = Button()
        btn.Text <- text
        btn.AutoSize <- true
        btn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        btn.MinimumSize <- Size(btn.Width, SettingsField.heightPx)
        btn.Dock <- DockStyle.Fill
        btn.TextAlign <- ContentAlignment.MiddleLeft
        btn.Margin <- Padding(5, marginPx, 0, marginPx)
        btn.Click.Add(fun _ -> onClick ())
        btn

    /// A content-sized action at the upper right of an edge page.
    let copyButton key onClick =
        let button = Button()
        button.Text <- Localization.getString key
        button.AutoSize <- true
        button.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        button.MinimumSize <- Size(0, SettingsField.heightPx)
        button.Anchor <- AnchorStyles.Right
        button.Margin <- Padding(0, marginPx, 0, marginPx)
        button.Click.Add(fun _ -> onClick ())
        button

    let pageHeader (check: CheckBox) key onClick =
        let header = new TableLayoutPanel(AutoSize = true, Dock = DockStyle.Top,
                                          Margin = Padding(0), ColumnCount = 2, RowCount = 1)
        header.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        header.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore
        header.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        header.Controls.Add(check, 0, 0)
        let copy = copyButton key onClick
        header.Controls.Add(copy, 1, 0)
        header, copy

    /// Two numeric fields and their reset buttons on the same row grid.
    let pairTable () =
        let pair = new TableLayoutPanel(AutoSize = true, Dock = DockStyle.Fill,
                                       Margin = Padding(0), ColumnCount = 6, RowCount = 1)
        pair.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        pair.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        for _ in 1..2 do
            pair.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore
            pair.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 50.0f)) |> ignore
            pair.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore
        pair

    let preparePair (pair: TableLayoutPanel) =
        for column in [0; 3] do
            let label = pair.GetControlFromPosition(column, 0)
            label.Margin <- Padding((if column = 0 then 0 else 30), marginPx, 5, marginPx)
            pair.GetControlFromPosition(column + 1, 0).MinimumSize <- Size(50, SettingsField.heightPx)
        for column in [2; 5] do
            let reset = pair.GetControlFromPosition(column, 0) :?> Button
            reset.AutoSizeMode <- AutoSizeMode.GrowAndShrink
            reset.MinimumSize <- Size(0, SettingsField.heightPx)
            reset.AutoSize <- false
            reset.Dock <- DockStyle.Right
            pair.ColumnStyles.[column].SizeType <- SizeType.Absolute

    let alignPairs (referenceReset: Control) (pairs: seq<TableLayoutPanel>) =
        // Preserve the existing width unless a paired reset caption needs more room.
        // Measure every pair so three-digit defaults do not clip and all rows align.
        let mutable syncingResetWidths = false
        let syncResetWidths () =
            if not syncingResetWidths then
                syncingResetWidths <- true
                try
                    let captionWidth =
                        pairs |> Seq.collect (fun pair ->
                            [2; 5] |> Seq.map (fun column ->
                                pair.GetControlFromPosition(column, 0).GetPreferredSize(Size.Empty).Width))
                        |> Seq.max
                    // A scaled reference button can exceed the pair's remaining space.
                    // Reserve the shared captions and minimum numeric fields before sizing resets.
                    let sharedCaptionWidth =
                        [0; 3] |> List.sumBy (fun column ->
                            pairs |> Seq.map (fun pair ->
                                let caption = pair.GetControlFromPosition(column, 0)
                                caption.GetPreferredSize(Size.Empty).Width + caption.Margin.Horizontal)
                            |> Seq.max)
                    let fittingWidth =
                        pairs |> Seq.map (fun pair ->
                            let fieldsWidth =
                                [1; 4] |> List.sumBy (fun column ->
                                    let field = pair.GetControlFromPosition(column, 0)
                                    field.MinimumSize.Width + field.Margin.Horizontal)
                            let resetMargins =
                                [2; 5] |> List.sumBy (fun column -> pair.GetControlFromPosition(column, 0).Margin.Horizontal)
                            (pair.ClientSize.Width - sharedCaptionWidth - fieldsWidth - resetMargins) / 2)
                        |> Seq.min
                    let requiredWidth = max captionWidth (min referenceReset.Width fittingWidth)
                    for pair in pairs do
                        pair.SuspendLayout()
                        try
                            for column in [2; 5] do
                                let reset = pair.GetControlFromPosition(column, 0)
                                // Dock right so a rounding pixel cannot widen the button.
                                if reset.Width <> requiredWidth then reset.Width <- requiredWidth
                                let width = float32 (requiredWidth + reset.Margin.Horizontal)
                                if pair.ColumnStyles.[column].Width <> width then
                                    pair.ColumnStyles.[column].Width <- width
                        finally
                            pair.ResumeLayout(true)
                finally
                    syncingResetWidths <- false
        referenceReset.SizeChanged.Add(fun _ -> syncResetWidths ())
        for pair in pairs do pair.Layout.Add(fun _ -> syncResetWidths ())
        syncResetWidths ()
        // Share caption widths across all paired rows, including after DPI changes.
        let mutable syncingCaptionWidths = false
        let syncCaptionWidths () =
            if not syncingCaptionWidths then
                syncingCaptionWidths <- true
                try
                    for column in [0; 3] do
                        let width =
                            pairs |> Seq.map (fun pair ->
                                let caption = pair.GetControlFromPosition(column, 0)
                                caption.GetPreferredSize(Size.Empty).Width + caption.Margin.Horizontal)
                            |> Seq.max |> float32
                        for pair in pairs do
                            let style = pair.ColumnStyles.[column]
                            style.SizeType <- SizeType.Absolute
                            if style.Width <> width then style.Width <- width
                finally
                    syncingCaptionWidths <- false
        for pair in pairs do pair.Layout.Add(fun _ -> syncCaptionWidths ())
        syncCaptionWidths ()

    // ComboBox ignores MinimumSize.Height, and its preferred size can lag
    // behind the native owner-drawn box after scaling. Report the closed
    // box's actual height so an AutoSize row centers it without overlap.
    type private ChoiceComboBox() =
        inherit ComboBox()
        override this.GetPreferredSize(proposedSize: Size) =
            let preferred = base.GetPreferredSize(proposedSize)
            Size(preferred.Width, max preferred.Height this.Height)

    /// A plain drop-down list, as the Behavior tab's.
    let choice (captions: string list) =
        let combo = new ChoiceComboBox() :> ComboBox
        combo.AutoSize <- true
        combo.DropDownStyle <- ComboBoxStyle.DropDownList
        SettingsField.apply combo
        for c in captions do combo.Items.Add(c) |> ignore
        combo.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        combo.Margin <- Padding(0, marginPx, 0, marginPx)
        combo

    /// The same radio rows and delay field on every edge page.
    let hideTabsControls (read: unit -> string * int) (save: string -> int -> unit) =
        let table = UIHelper.settingsRadioGroup 3 3
        table.Padding <- Padding(0)
        table.Margin <- Padding(0)
        let delay = new TextBox(Width = 60, Anchor = AnchorStyles.Left, Margin = Padding(0))
        SettingsField.apply delay
        let label = caption (Localization.getString "HideTabsDelayMilliseconds")
        label.Margin <- Padding(10, 0, 3, 0)
        let radios =
            ["never", "HideTabsNever"; "down", "HideTabsWhenDown"; "doubleclick", "HideTabsOnClick"]
            |> List.mapi (fun row (mode, key) ->
                let radio = UIHelper.settingsRadio key
                table.Controls.Add(radio, 0, row)
                mode, radio)
        table.Controls.Add(label, 1, 1)
        table.Controls.Add(delay, 2, 1)
        let mutable refreshing = false
        let refresh () =
            refreshing <- true
            try
                let mode, milliseconds = read ()
                for value, radio in radios do radio.Checked <- (value = TabEdgeSettings.normalizeHideMode mode)
                delay.Text <- string milliseconds
                delay.Enabled <- (mode = "down")
            finally refreshing <- false
        refresh ()
        for mode, radio in radios do
            radio.CheckedChanged.Add(fun _ ->
                if radio.Checked && not refreshing then
                    let current, milliseconds = read ()
                    if mode <> current then save mode milliseconds
                    delay.Enabled <- (mode = "down"))
        delay.LostFocus.Add(fun _ ->
            if not refreshing then
                let mode, previous = read ()
                match Int32.TryParse delay.Text with
                | true, value when value >= 0 && value <= 10000 ->
                    if value <> previous then save mode value
                | _ -> delay.Text <- string previous)
        table, refresh

    /// A number field with IntEditor's range.
    let number () =
        let n = SettingsNumericUpDown.Create()
        n.Minimum <- 1m
        n.Maximum <- 1000m
        n.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        n.Margin <- Padding(0, marginPx, 0, marginPx)
        n

    /// Show a value the field cannot hold as the nearest one it can.
    let setNumber (n: NumericUpDown) (value: int) =
        n.Value <- decimal (max (int n.Minimum) (min (int n.Maximum) value))

    /// Caption key of each tab shape. The three lines are named across on a
    /// left or right strip written across (8), where they lie across it.
    let shapeCaption (across: bool) (shape: string) =
        match TabShape.normalize shape with
        | TabShape.strong -> if across then "TabShapeStrongHorizontal" else "TabShapeStrong"
        | TabShape.slant -> "TabShapeSlant"
        | TabShape.rounded -> if across then "TabShapeRoundedHorizontal" else "TabShapeRounded"
        | TabShape.angular -> if across then "TabShapeAngularHorizontal" else "TabShapeAngular"
        | TabShape.cutLeft -> "TabShapeCutLeft"
        | TabShape.cutRight -> "TabShapeCutRight"
        | TabShape.rectangle -> "TabShapeRectangle"
        | TabShape.roundedRectangle -> "TabShapeRoundedRectangle"
        | TabShape.strongRoundedRectangle -> "TabShapeStrongRoundedRectangle"
        | TabShape.angularRectangle -> "TabShapeAngularRectangle"
        | _ -> "TabShapeSCurve"

    /// The production outline in physical edge coordinates. Auto placement is
    /// represented outside; this settings page has no target window or monitor.
    let previewOutline edge orientation inside shape width height band =
        let sideways = not (TabEdges.runsHorizontally edge)
        if sideways && orientation = Some TabEdges.SideText.Horizontal then
            TabShape.sideOutline shape width height ((edge = TabEdges.Edge.Left) <> inside) band
        else
            let pointsDown = if edge = TabEdges.Edge.Top then inside else not inside
            let bottom, top = if pointsDown then -1.0f, height - 1.0f else height, 0.0f
            TabShape.outline shape width bottom top band

    /// Uniformly fit the physical strip into the item height. Do not stretch
    /// its corners or reuse a rotated top outline for horizontal side tabs.
    let drawTabShapePreview edge orientation inside (g: Graphics) (shape: string) (x: int) (y: int) (height: int) (count: int) (overlap: int) (color: Color) (background: Color) =
        let unit = float32 height / 24.0f
        let band = float32 TabShape.edgeBand * unit
        let h = float32 height
        let w = h * 2.5f
        let sideways = not (TabEdges.runsHorizontally edge)
        let across = sideways && orientation = Some TabEdges.SideText.Horizontal
        let before, after = TabShape.sideMargins (int w) (int band)
        let rowLength = h + float32 (before + after)
        let physicalWidth, physicalHeight =
            if across then w, rowLength
            else w,h
        let along = if sideways then physicalHeight else physicalWidth
        let effectiveOverlap =
            if across then min (rowLength - unit) (float32 overlap * unit)
            else max (-along / 2.0f) (min (along / 2.0f) (float32 overlap * unit))
        let step = along - effectiveOverlap
        let total = float32 (count - 1) * step + along
        let fit = if sideways then h / total else 1.0f
        let state = g.Save()
        g.SmoothingMode <- Drawing2D.SmoothingMode.AntiAlias
        g.TranslateTransform(float32 x, float32 y)
        g.ScaleTransform(fit, fit)
        use pen = new Pen(color, 1.0f / fit)
        // A row written across reaches two screen pixels past each end of the
        // baseline's row, so its body is not drawn smaller than it reads, and
        // one more past the lower end, so it does not look raised.
        let reach = if across then 2.0f / fit else 0.0f
        let lowerReach = if across then 3.0f / fit else 0.0f
        for i in 0 .. count - 1 do
            use path = new Drawing2D.GraphicsPath()
            previewOutline edge orientation inside shape w (if across then rowLength + reach + lowerReach else h) band |> TabShape.addToPath path
            use m = new Drawing2D.Matrix()
            m.Translate((if sideways then 0.0f else float32 i * step), (if sideways then float32 i * step - reach else 0.0f))
            path.Transform(m)
            use cover = new SolidBrush(Color.FromArgb(255, background))
            use tint = new SolidBrush(Color.FromArgb((if i = count - 1 then 90 else 40), color))
            g.FillPath(cover, path)
            g.FillPath(tint, path)
            g.DrawPath(pen, path)
        if sideways then
            let baseline = if (edge = TabEdges.Edge.Left) <> inside then physicalWidth else -1.0f
            g.DrawLine(pen, baseline, -2.0f, baseline, total + 2.0f)
        else
            let pointsDown = if edge = TabEdges.Edge.Top then inside else not inside
            let baseline = if pointsDown then -1.0f else h
            g.DrawLine(pen, -2.0f, baseline, total + 2.0f, baseline)
        g.Restore(state)
        int (ceil ((if sideways then physicalWidth else total) * fit)) + 2

    /// Side pages written across show one tab in both the closed field and
    /// the open list. Vertical side tabs are previewed as top tabs, and every
    /// other list retains two neighbours to illustrate overlap.
    /// Every preview uses the production outline and current editor values.
    let shapeCombo edge (shapes: unit -> (TabEdges.SideText option * string) list) (overlap: unit -> int) =
        let combo = new DarkMode.PreviewComboBox()
        let sideways = not (TabEdges.runsHorizontally edge)
        combo.DropDownStyle <- ComboBoxStyle.DropDownList
        // Variable owner-draw, as the color theme combo: the closed box keeps
        // the height of a plain drop-down, the open list gets items tall
        // enough for a picture that tells the shapes apart at 100%.
        // ItemHeight (the closed box) is a 96-dpi design number, so
        // SettingsDpi scales it from that; left to the font it is re-derived
        // from the already scaled font and then scaled again. 14 is a plain
        // drop-down list's own item height at the dialog font, so the box is
        // as tall as the Behavior tab's.
        combo.DrawMode <- DrawMode.OwnerDrawVariable
        combo.ItemHeight <- SettingsField.comboItemHeightPx
        // Outside Bounds, so Control.Scale never sees it (see the color
        // theme combo's MeasureItem).
        let listItemHeight () = SettingsDpi.px 30
        combo.MeasureItem.Add <| fun e -> e.ItemHeight <- listItemHeight ()
        combo.IntegralHeight <- false
        let fitDropDown() =
            // Recalculate on every opening, including after a monitor DPI change.
            combo.MaxDropDownItems <- max 1 combo.Items.Count
            let rowsHeight =
                [0 .. combo.Items.Count - 1]
                |> List.sumBy (fun index -> max (listItemHeight ()) (combo.GetItemHeight(index)))
            combo.DropDownHeight <- rowsHeight + SettingsDpi.px 4
            let textWidth =
                combo.Items |> Seq.cast<obj>
                |> Seq.map (fun item -> TextRenderer.MeasureText(string item, combo.Font).Width)
                |> Seq.fold max 0
            combo.DropDownWidth <- max combo.Width (textWidth + SettingsDpi.px 140)
        combo.DropDown.Add(fun _ -> fitDropDown())
        combo.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        combo.Margin <- Padding(0, marginPx, 0, marginPx)
        // Colors as in the color theme combo's DrawItem: DarkMode leaves the
        // drawing of an owner-drawn combo to its owner.
        combo.DrawItem.Add <| fun e ->
            let darkOn = isDarkModeEnabled()
            let isSelected = (e.State &&& DrawItemState.Selected) <> DrawItemState.None
            let background =
                if darkOn then (if isSelected then DarkMode.darkAccent else DarkMode.darkPanel)
                else e.BackColor
            if darkOn then
                use bg = new SolidBrush(background)
                e.Graphics.FillRectangle(bg, e.Bounds)
            else
                e.DrawBackground()
            let listed = shapes ()
            if e.Index >= 0 && e.Index < listed.Length && e.Index < combo.Items.Count then
                let textColor = if darkOn then DarkMode.darkText else e.ForeColor
                let pad = max 2 (e.Bounds.Height / 6)
                let previewHeight = max 4 (e.Bounds.Height - 2 * pad - 1)
                // Every list uses the top tab's item height. Rows written across
                // show one tab per item; the other lists show two neighbours.
                let isEdit = (e.State &&& DrawItemState.ComboBoxEdit) <> DrawItemState.None
                let previewWidth =
                    let orientation, shape = listed.[e.Index]
                    let across = sideways && orientation = Some TabEdges.SideText.Horizontal
                    let asTop = sideways && not across
                    // A row written across is shrunk to fit its end margins, so it
                    // gets one more pixel above and below.
                    let grow = if across then 1 else 0
                    drawTabShapePreview (if asTop then TabEdges.Edge.Top else edge) orientation
                        false e.Graphics shape
                        (e.Bounds.Left + pad + 2) (e.Bounds.Top + pad - grow) (previewHeight + 2 * grow)
                        (if isEdit || across then 1 else 2) (overlap ()) textColor background
                let textBounds =
                    Rectangle(e.Bounds.Left + pad + 2 + previewWidth + pad * 2, e.Bounds.Top,
                              max 0 (e.Bounds.Width - previewWidth - pad * 4 - 2), e.Bounds.Height)
                TextRenderer.DrawText(
                    e.Graphics, combo.Items.[e.Index].ToString(), e.Font, textBounds, textColor,
                    TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter ||| TextFormatFlags.EndEllipsis)
            e.DrawFocusRectangle()
        combo, fitDropDown

    /// "Icon only" / "Specify width" and the width, in one cell.
    type PinnedWidth = {
        panel: TableLayoutPanel
        iconOnly: RadioButton
        specify: RadioButton
        width: SettingsNumericUpDown
    }

    let pinnedWidth () =
        let iconOnly = RadioButton()
        let specify = RadioButton()
        let width = SettingsNumericUpDown.Create()
        let tbl = TableLayoutPanel()
        tbl.ColumnCount <- 3
        tbl.RowCount <- 1
        tbl.Dock <- DockStyle.Fill
        tbl.Margin <- Padding(0, marginPx, 0, marginPx)
        // A nested container in an AutoSize row must be AutoSize itself, or
        // the row takes its 200 x 100 default (see AppearanceView).
        tbl.AutoSize <- true
        tbl.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        tbl.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore   // Icon-only radio
        tbl.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore   // Specify radio
        tbl.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore  // NumericUpDown (fill)
        tbl.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        iconOnly.Text <- Localization.getString("PinnedWidthIconOnly")
        iconOnly.AutoSize <- true
        iconOnly.Margin <- Padding(0, 0, 10, 3)
        iconOnly.Anchor <- AnchorStyles.Left
        specify.Text <- Localization.getString("PinnedWidthSpecify")
        specify.AutoSize <- true
        specify.Margin <- Padding(0, 0, 5, 3)
        specify.Anchor <- AnchorStyles.Left
        width.Minimum <- 50m
        width.Maximum <- 500m
        width.Value <- 90m
        width.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        width.Margin <- Padding(0)
        tbl.Controls.Add(iconOnly, 0, 0)
        tbl.Controls.Add(specify, 1, 0)
        tbl.Controls.Add(width, 2, 0)
        ({ panel = tbl; iconOnly = iconOnly; specify = specify; width = width } : PinnedWidth)

    /// A distance row's two ends: [first][number][reset] [second][number]
    /// [reset] - [left][right] on a top or bottom edge, [top][bottom] on a
    /// left or right one (8). It spans the input and reset columns. What a
    /// reset button does is the caller's.
    type DistancePair = {
        panel: TableLayoutPanel
        first: SettingsNumericUpDown
        second: SettingsNumericUpDown
        firstReset: Button
        secondReset: Button
    }

    let distancePair (firstCaption: string) (secondCaption: string) (resetValue: int) =
        let tbl = TableLayoutPanel()
        tbl.ColumnCount <- 6
        tbl.RowCount <- 1
        tbl.Dock <- DockStyle.Fill
        tbl.Margin <- Padding(0)
        tbl.AutoSize <- true
        tbl.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        for percent in [ None; Some 50.0f; None; None; Some 50.0f; None ] do
            match percent with
            | Some(p) -> tbl.ColumnStyles.Add(ColumnStyle(SizeType.Percent, p)) |> ignore
            | None -> tbl.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore
        tbl.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        let side (text: string) (column: int) (leftGap: int) =
            let label = caption text
            label.Margin <- Padding(leftGap, marginPx, 5, marginPx)
            let n = number ()
            let reset = Button()
            reset.Text <- resetText (string resetValue)
            reset.AutoSize <- true
            reset.AutoSizeMode <- AutoSizeMode.GrowAndShrink
            reset.MinimumSize <- Size(0, SettingsField.heightPx)
            reset.TextAlign <- ContentAlignment.MiddleLeft
            reset.Anchor <- AnchorStyles.Left
            reset.Margin <- Padding(5, marginPx, 0, marginPx)
            tbl.Controls.Add(label, column, 0)
            tbl.Controls.Add(n, column + 1, 0)
            tbl.Controls.Add(reset, column + 2, 0)
            n, reset
        let first, firstReset = side firstCaption 0 0
        let second, secondReset = side secondCaption 3 15
        ({ panel = tbl; first = first; second = second; firstReset = firstReset; secondReset = secondReset } : DistancePair)

/// The panel of an edge other than the top on the Appearance tab (8): its
/// own tab appearance and layout, kept under "TabEdge<name>" of the
/// settings file (TabEdgeSettings). Where the file has no value the panel
/// shows the edge's default appearance and offered text orientation,
/// and writes nothing until
/// the person changes it. The top edge's panel is AppearanceView's own.
type EdgeSettingsPanel(edge: TabEdges.Edge, check: CheckBox, copied: unit -> unit) as this =
    // Left and right edges have a separate writing-mode picker.
    let sideways = not (TabEdges.runsHorizontally edge)
    let mutable refreshing = false
    let defaults () = Services.program.defaultTabAppearanceInfo
    let values () =
        try TabEdgeSettings.valuesOf Services.settings.root edge
        with _ -> TabEdgeSettings.noValues
    let write (f: JObject -> unit) =
        if not refreshing then
            let root = Services.settings.root
            f root
            Services.settings.root <- root
            TabEdgeSettings.changed.Trigger()
    let text key = Localization.getString key

    let rowCount = if sideways then 7 else 6
    let table = EdgeRows.rowTable rowCount
    let header, copyButton =
        let key =
            match edge with
            | TabEdges.Edge.Left -> "ApplySameSettingsToRightEdge"
            | TabEdges.Edge.Right -> "ApplySameSettingsToLeftEdge"
            | _ -> "ApplySameSettingsToTopEdge"
        EdgeRows.pageHeader check key (fun () -> this.copyToOpposite())
    let panel = new TableLayoutPanel(AutoSize = true, Dock = DockStyle.Top,
                                    Margin = Padding(0), ColumnCount = 1, RowCount = 3)
    do
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        for _ in 1..3 do panel.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        panel.Controls.Add(header, 0, 0)
        panel.Controls.Add(table, 0, 1)
    let mutable nextRow = 0
    let row (captionKey: string) (input: Control) (reset: Control option) =
        let r = nextRow
        nextRow <- nextRow + 1
        EdgeRows.place table (EdgeRows.caption (text captionKey)) 0 r
        EdgeRows.place table input 1 r
        reset |> Option.iter (fun b -> EdgeRows.place table b 2 r)
        r

    let mutable listedShapes = []
    let mutable pinnedRow = -1

    let heightInput = EdgeRows.number ()
    let widthInput = EdgeRows.number ()
    let overlapInput = EdgeRows.number ()
    let pinned = EdgeRows.pinnedWidth ()
    let textBottomCombo = EdgeRows.choice [ text "SideTextBottomInside"; text "SideTextBottomOutside" ]
    let textCombo = EdgeRows.choice [ text "SideTextVertical"; text "SideTextHorizontal" ]
    // Keep the writing mode and baseline direction on the same row.
    let textOptions = new TableLayoutPanel(AutoSize = true, Dock = DockStyle.Fill,
                                           Margin = Padding(0), ColumnCount = 2, RowCount = 1)
    do
        let textWidth =
            [textCombo; textBottomCombo]
            |> Seq.collect (fun combo ->
                combo.Items |> Seq.cast<obj>
                |> Seq.map (fun item -> TextRenderer.MeasureText(string item, combo.Font).Width))
            |> Seq.max
        let width = textWidth + SettingsDpi.px 28
        let gap = SettingsDpi.px 8
        for combo in [textCombo; textBottomCombo] do
            combo.MinimumSize <- Size(width, 0)
            combo.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        textOptions.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, float32(width + gap))) |> ignore
        textOptions.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        textCombo.Margin <- Padding(0, EdgeRows.marginPx, gap, EdgeRows.marginPx)
        textOptions.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        textOptions.Controls.Add(textCombo, 0, 0)
        textOptions.Controls.Add(textBottomCombo, 1, 0)
    let alignCombo =
        EdgeRows.choice (if sideways then [ text "AlignTop"; text "AlignBottom" ] else [ text "AlignTopLeft"; text "AlignTopRight" ])
    let directionCombo = EdgeRows.choice [ text "TabVerticalAuto"; text "TabVerticalAlwaysDown" ]
    let shapeCombo, fitShapes =
        EdgeRows.shapeCombo edge
            (fun () -> listedShapes) (fun () -> int overlapInput.Value)
    let hideTabsRadio, refreshHideTabs =
        EdgeRows.hideTabsControls
            (fun () -> TabEdgeSettings.hideSettings Services.settings.root edge)
            (fun mode delay -> write (fun root ->
                let currentMode, currentDelay = TabEdgeSettings.hideSettings root edge
                // Preserve inheritance for the field the user did not change.
                if mode <> currentMode then
                    TabEdgeSettings.writeString root edge "hideTabsWhenDownByDefault" mode
                if delay <> currentDelay then
                    TabEdgeSettings.writeInt root edge "hideTabsDelayMilliseconds" delay))
    let placementPanel =
        let grid = UIHelper.form (List2([
            "TabPositionByDefault", alignCombo :> Control
            "TabVerticalDirection", directionCombo :> Control
            "HideTabsWhenDownByDefault", hideTabsRadio :> Control ]))
        grid.Dock <- DockStyle.Top
        grid.Margin <- Padding(0, UIHelper.settingsRowHeightPx / 2, 0, 0)
        grid.AutoScroll <- false
        for combo in [alignCombo; directionCombo] do
            combo.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        grid
    do panel.Controls.Add(placementPanel, 0, 2)
    let firstEnd, secondEnd = if sideways then "EdgeTop", "EdgeBottom" else "EdgeLeft", "EdgeRight"
    let outside = EdgeRows.distancePair (text firstEnd) (text secondEnd) TabEdges.defaultOutsideDistance
    let inside = EdgeRows.distancePair (text firstEnd) (text secondEnd) TabEdges.defaultInsideDistance

    let defaultText = (TabEdges.newEdgeLayout edge).text
    let defaultShape () = TabEdgeSettings.edgeShape edge TabEdgeSettings.noValues (defaults ()).tabShape
    let selectedStyle () =
        let i = shapeCombo.SelectedIndex
        if i >= 0 && i < listedShapes.Length then listedShapes.[i]
        else defaultText, defaultShape ()
    let sideText () = fst (selectedStyle ())
    let shownShape () = snd (selectedStyle ())
    let saveShape () =
        write (fun root ->
            sideText () |> Option.iter (fun t -> TabEdgeSettings.writeString root edge "tabText" (TabEdgeSettings.textName t))
            TabEdgeSettings.writeString root edge "tabShape" (shownShape ()))

    let showShapes orientation (shape: string) =
        let previous = refreshing
        refreshing <- true
        try
            if sideways then textCombo.SelectedIndex <- if orientation = Some TabEdges.SideText.Horizontal then 1 else 0
            listedShapes <- TabEdgeSettings.shapesFor orientation |> List.map (fun s -> orientation, s)
            shapeCombo.Items.Clear()
            for direction, s in listedShapes do
                shapeCombo.Items.Add(text (EdgeRows.shapeCaption (direction = Some TabEdges.SideText.Horizontal) s)) |> ignore
            let horizontal = sideways && orientation = Some TabEdges.SideText.Horizontal
            textBottomCombo.Visible <- not horizontal
            if pinnedRow >= 0 then
                table.SuspendLayout()
                for column in 0 .. 2 do
                    table.GetControlFromPosition(column, pinnedRow).Visible <- not horizontal
                table.RowStyles.[pinnedRow].SizeType <- if horizontal then SizeType.Absolute else SizeType.AutoSize
                table.RowStyles.[pinnedRow].Height <- 0.0f
                table.ResumeLayout(true)
            let shown = orientation, TabEdgeSettings.shapeFor orientation shape
            shapeCombo.SelectedIndex <- List.findIndex ((=) shown) listedShapes
            fitShapes ()
        finally refreshing <- previous

    do
        let d = defaults ()
        if sideways then
            row "Tab Shape" textOptions None |> ignore
            table.SetColumnSpan(textOptions, 2)
            EdgeRows.place table shapeCombo 1 nextRow
            nextRow <- nextRow + 1
        else
            row "Tab Shape" shapeCombo None |> ignore
        table.SetColumnSpan(shapeCombo, 2)
        let numberRow captionKey (input: SettingsNumericUpDown) (defaultValue: int) =
            let reset = EdgeRows.resetButton (EdgeRows.resetText (string defaultValue)) (fun () -> input.Value <- decimal defaultValue)
            row captionKey input (Some(reset :> Control)) |> ignore
        let sizePair = EdgeRows.pairTable ()
        for column, caption, input, value in
            [0, "Height", heightInput, d.tabHeight; 3, "Width (max)", widthInput, d.tabMaxWidth] do
            let reset = EdgeRows.resetButton (EdgeRows.resetText (string value)) (fun () -> input.Value <- decimal value)
            EdgeRows.place sizePair (EdgeRows.caption (text caption)) column 0
            EdgeRows.place sizePair input (column + 1) 0
            EdgeRows.place sizePair reset (column + 2) 0
        EdgeRows.preparePair sizePair
        row "Tab size" sizePair None |> ignore
        table.SetColumnSpan(sizePair, 2)
        let pinnedReset =
            EdgeRows.resetButton (EdgeRows.resetText (string d.tabPinnedTabWidth)) (fun () ->
                pinned.width.Value <- decimal d.tabPinnedTabWidth)
        pinnedRow <- row "Pinned Tab Width" pinned.panel (Some(pinnedReset :> Control))
        numberRow "Tab Overlap" overlapInput d.tabOverlap
        // The distance rows carry their own reset buttons, across both columns.
        row "Indent Normal" outside.panel None |> ignore
        row "Indent Flipped" inside.panel None |> ignore
        table.SetColumnSpan(outside.panel, 2)
        table.SetColumnSpan(inside.panel, 2)
        EdgeRows.preparePair outside.panel
        EdgeRows.preparePair inside.panel
        EdgeRows.alignPairs pinnedReset [sizePair; outside.panel; inside.panel]
        if sideways then
            // Split the pickers where the distance rows split: the writing mode
            // ends with the first end's reset, the text side starts at the second end.
            outside.panel.Layout.Add(fun _ ->
                let secondCaption = outside.panel.GetControlFromPosition(3, 0)
                let gap = secondCaption.Margin.Left
                if textCombo.Margin.Right <> gap then
                    textCombo.Margin <- Padding(0, EdgeRows.marginPx, gap, EdgeRows.marginPx)
                let x = max (textCombo.MinimumSize.Width + gap) secondCaption.Left
                if int textOptions.ColumnStyles.[0].Width <> x then
                    textOptions.ColumnStyles.[0].Width <- float32 x)

        // Every change is written at once, as the rest of the dialog does.
        shapeCombo.SelectedIndexChanged.Add(fun _ -> saveShape ())
        textCombo.SelectedIndexChanged.Add(fun _ ->
            if not refreshing then
                let orientation = Some(if textCombo.SelectedIndex = 1 then TabEdges.SideText.Horizontal else TabEdges.SideText.Vertical)
                showShapes orientation (shownShape ())
                saveShape ())
        textBottomCombo.SelectedIndexChanged.Add(fun _ ->
            write (fun root -> TabEdgeSettings.writeBool root edge "tabTextBottomInside" (textBottomCombo.SelectedIndex <> 1)))
        let numberKey (input: NumericUpDown) (key: string) =
            input.ValueChanged.Add(fun _ -> write (fun root -> TabEdgeSettings.writeInt root edge key (int input.Value)))
        numberKey heightInput "tabHeight"
        numberKey widthInput "tabMaxWidth"
        numberKey overlapInput "tabOverlap"
        numberKey pinned.width "tabPinnedTabWidth"
        numberKey outside.first "tabOutsideDistanceStart"
        numberKey outside.second "tabOutsideDistanceEnd"
        numberKey inside.first "tabInsideDistanceStart"
        numberKey inside.second "tabInsideDistanceEnd"
        for pair, resetValue in [ outside, TabEdges.defaultOutsideDistance; inside, TabEdges.defaultInsideDistance ] do
            pair.firstReset.Click.Add(fun _ -> pair.first.Value <- decimal resetValue)
            pair.secondReset.Click.Add(fun _ -> pair.second.Value <- decimal resetValue)
        // The pictures in the shape list follow the overlap.
        overlapInput.ValueChanged.Add(fun _ -> shapeCombo.Invalidate())
        pinned.iconOnly.CheckedChanged.Add(fun _ ->
            pinned.width.Enabled <- pinned.specify.Checked
            write (fun root -> TabEdgeSettings.writeBool root edge "tabPinnedTabWidthIcon" pinned.iconOnly.Checked))
        alignCombo.SelectedIndexChanged.Add(fun _ ->
            let align = if alignCombo.SelectedIndex = 1 then TabEdges.AlignEnd else TabEdges.AlignStart
            write (fun root -> TabEdgeSettings.writeString root edge "tabAlignment" (TabEdgeSettings.alignName align)))
        directionCombo.SelectedIndexChanged.Add(fun _ ->
            let direction = if directionCombo.SelectedIndex = 1 then TabEdges.Direction.AlwaysInside else TabEdges.Direction.Outside
            write (fun root -> TabEdgeSettings.writeString root edge "tabDirection" (TabEdgeSettings.directionName direction)))

    member this.edge = edge
    member this.control = panel :> Control

    member this.setEnabled(enabled) =
        table.Enabled <- enabled
        placementPanel.Enabled <- enabled
        copyButton.Enabled <- enabled

    member private this.copyToOpposite() =
        let source = { TabEdgeSettings.noValues with
                          hideMode = Some(fst (TabEdgeSettings.hideSettings Services.settings.root edge))
                          hideDelay = Some(snd (TabEdgeSettings.hideSettings Services.settings.root edge))
                          tabHeight = Some(int heightInput.Value)
                          tabMaxWidth = Some(int widthInput.Value)
                          tabPinnedTabWidth = Some(int pinned.width.Value)
                          tabPinnedTabWidthIcon = Some pinned.iconOnly.Checked
                          tabOverlap = Some(int overlapInput.Value)
                          tabShape = Some(shownShape ())
                          text = sideText ()
                          textBottomInside = if sideways then Some(textBottomCombo.SelectedIndex <> 1) else None
                          align = Some(if alignCombo.SelectedIndex = 1 then TabEdges.AlignEnd else TabEdges.AlignStart)
                          direction = Some(if directionCombo.SelectedIndex = 1 then TabEdges.Direction.AlwaysInside else TabEdges.Direction.Outside)
                          outsideStart = Some(int outside.first.Value)
                          outsideEnd = Some(int outside.second.Value)
                          insideStart = Some(int inside.first.Value)
                          insideEnd = Some(int inside.second.Value) }
        if not refreshing then
            let previousTopValues =
                if edge = TabEdges.Edge.Bottom then
                    ["tabAppearance"; "tabPositionByDefault"; "tabVerticalDirection"; "hideTabsWhenDownByDefault"; "hideTabsDelayMilliseconds"]
                    |> List.map (fun key -> key, Services.settings.getValue(key))
                else []
            let root = Services.settings.root
            let result = TabEdgeSettings.copyOppositeValues root edge source
            let changed = not (JToken.DeepEquals(root, result))
            if changed then Services.settings.root <- result
            // Unchanged alignment must preserve each group's own alignment.
            for key, previous in previousTopValues do
                let current = Services.settings.getValue(key)
                if current <> previous then Services.settings.setValue(key, current)
            if changed then TabEdgeSettings.changed.Trigger()
            copied()

    /// Show the settings file's values; writes nothing.
    member this.refresh() =
        let previous = refreshing
        refreshing <- true
        try
            refreshHideTabs ()
            let v = values ()
            textBottomCombo.SelectedIndex <- if defaultArg v.textBottomInside true then 0 else 1
            let d = defaults ()
            let layout = TabEdgeSettings.layoutOf v (TabEdges.newEdgeLayout edge)
            showShapes layout.text (TabEdgeSettings.edgeShape edge v d.tabShape)
            EdgeRows.setNumber heightInput (defaultArg v.tabHeight d.tabHeight)
            EdgeRows.setNumber widthInput (defaultArg v.tabMaxWidth d.tabMaxWidth)
            EdgeRows.setNumber overlapInput (defaultArg v.tabOverlap d.tabOverlap)
            let iconOnly = defaultArg v.tabPinnedTabWidthIcon d.tabPinnedTabWidthIcon
            pinned.iconOnly.Checked <- iconOnly
            pinned.specify.Checked <- not iconOnly
            pinned.width.Enabled <- not iconOnly
            EdgeRows.setNumber pinned.width (defaultArg v.tabPinnedTabWidth d.tabPinnedTabWidth)
            alignCombo.SelectedIndex <- if layout.defaultAlign = TabEdges.AlignEnd then 1 else 0
            directionCombo.SelectedIndex <- if layout.direction = TabEdges.Direction.AlwaysInside then 1 else 0
            EdgeRows.setNumber outside.first (fst layout.outsideDistance)
            EdgeRows.setNumber outside.second (snd layout.outsideDistance)
            EdgeRows.setNumber inside.first (fst layout.insideDistance)
            EdgeRows.setNumber inside.second (snd layout.insideDistance)
        finally refreshing <- previous
