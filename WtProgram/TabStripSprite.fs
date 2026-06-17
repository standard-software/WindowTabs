namespace Bemo
open System
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.Windows.Forms

type IconSprite = {
    icon: Icon
    size: Sz
    } with
    interface ISprite with
        member this.image =
            let bitmap = Img(this.size)
            let g = bitmap.graphics
            // Fill with near-transparent color so entire icon area is hit-testable
            g.Clear(Color.FromArgb(1, 0, 0, 0))
            try
                // Draw icon with proper scaling to fit the target size
                let rect = new Rectangle(0, 0, this.size.width, this.size.height)
                do g.DrawIcon(this.icon, rect)
            with | e -> ()
            bitmap
        member this.children = List2()

type CloseButtonSprite = {
    hover: bool
    captured: bool
    size: Sz
    }
    with
    member private this.bgColor =
        match this.hover, this.captured with
        | true, true -> Some(Color.FromArgb(128, 128, 128))
        | true, false -> Some(Color.FromArgb(90, 90, 90))
        | _ -> None
    member private this.penColor = if this.bgColor.IsSome then Color.White else Color.Gray
    member private this.pen = new Pen(this.penColor, 1.7f)
    interface ISprite with
        member this.image =
            let crossOffset = 3
            let bitmap = Img(this.size)
            let g = bitmap.graphics
            g.SmoothingMode <- SmoothingMode.AntiAlias
            // Draw rounded rectangle background
            let bgColor = this.bgColor.def(Color.FromArgb(1, 1, 1, 1))
            let r = 3
            let rect = Rectangle(0, 0, this.size.width - 1, this.size.height - 1)
            use path = new GraphicsPath()
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180.0f, 90.0f)
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270.0f, 90.0f)
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0.0f, 90.0f)
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90.0f, 90.0f)
            path.CloseFigure()
            g.FillPath(new SolidBrush(bgColor), path)
            // Draw X mark centered on rounded rect
            let cx = float32(this.size.width - 1) / 2.0f
            let cy = float32(this.size.height - 1) / 2.0f
            let half = 4.0f
            g.DrawLine(this.pen, cx - half, cy - half, cx + half, cy + half)
            g.DrawLine(this.pen, cx - half, cy + half, cx + half, cy - half)
            bitmap
        member this.children = List2()

// Pin icon font: use Segoe MDL2 Assets (Windows 10/11 system icon font)
module private PinIcon =
    let font : Font option =
        try
            let f = new Font("Segoe MDL2 Assets", 12.0f)
            if f.Name = "Segoe MDL2 Assets" then Some(f)
            else f.Dispose(); None
        with _ -> None

type PinButtonSprite = {
    hover: bool
    captured: bool
    size: Sz
    }
    with
    member private this.bgColor =
        match this.hover, this.captured with
        | true, true -> Some(Color.FromArgb(128, 128, 128))
        | true, false -> Some(Color.FromArgb(90, 90, 90))
        | _ -> None
    member private this.penColor = if this.bgColor.IsSome then Color.White else Color.Gray
    interface ISprite with
        member this.image =
            let bitmap = Img(this.size)
            let g = bitmap.graphics
            g.SmoothingMode <- SmoothingMode.AntiAlias
            // Draw rounded rectangle background (same as close button)
            let bgColor = this.bgColor.def(Color.FromArgb(1, 1, 1, 1))
            let r = 3
            let rect = Rectangle(0, 0, this.size.width - 1, this.size.height - 1)
            use path = new GraphicsPath()
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180.0f, 90.0f)
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270.0f, 90.0f)
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0.0f, 90.0f)
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90.0f, 90.0f)
            path.CloseFigure()
            g.FillPath(new SolidBrush(bgColor), path)
            // Draw pin icon
            let color = this.penColor
            use brush = new SolidBrush(color)
            match PinIcon.font with
            | Some font ->
                // Use Segoe MDL2 Assets system font for pin icon (E718 = Pin glyph)
                // Rotate -45 degrees (left tilt) around center
                let cx = float32 this.size.width / 2.0f
                let cy = float32 this.size.height / 2.0f
                g.TextRenderingHint <- System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
                g.TranslateTransform(cx, cy)
                g.RotateTransform(-45.0f)
                g.TranslateTransform(-cx, -cy)
                use format = new StringFormat()
                format.Alignment <- StringAlignment.Center
                format.LineAlignment <- StringAlignment.Center
                let drawRect = RectangleF(-1.0f, 2.0f, float32 this.size.width, float32 this.size.height)
                g.DrawString("\uE718", font, brush, drawRect, format)
                g.ResetTransform()
            | None ->
                // Fallback: simple diagonal pushpin using GDI+
                let cx = float32(this.size.width - 1) / 2.0f
                let cy = float32(this.size.height - 1) / 2.0f
                let dc = 0.707f
                let ptf s p = PointF(cx + (s + p) * dc, cy + (s - p) * dc)
                use headPen = new Pen(color, 1.8f)
                headPen.StartCap <- Drawing2D.LineCap.Round
                headPen.EndCap <- Drawing2D.LineCap.Round
                g.DrawLine(headPen, ptf -3.5f -4.0f, ptf -3.5f 4.0f)
                use bodyPath = new GraphicsPath()
                bodyPath.AddPolygon([|
                    ptf -3.5f -2.0f
                    ptf -3.5f  2.0f
                    ptf  1.0f  0.7f
                    ptf  1.0f -0.7f
                |])
                g.FillPath(brush, bodyPath)
                use needlePen = new Pen(color, 1.2f)
                g.DrawLine(needlePen, ptf 1.0f 0.0f, ptf 5.0f 0.0f)
            bitmap
        member this.children = List2()

type TabDisplayInfo = {
    bgColor : Color option
    fillColor : Color option
    underlineColor : Color option
    borderColor : Color option
    text: string
    textFont: Font
    textBrush: Brush
    icon: Icon
    }
    
type TabSprite<'id> = {
    id: 'id
    isTop: bool
    isSelected: bool
    appearance: TabAppearanceInfo
    displayInfo: TabDisplayInfo
    size: Sz
    onlyIcon: bool
    isPinned: bool
    direction: TabDirection
    hover: TabPart option
    captured: TabPart option
    } with

    member private this.iconSprite =
        {
            IconSprite.icon = this.displayInfo.icon
            size = this.iconSize
        } :> ISprite
    
    member private this.closeButtonSprite =
        {
            CloseButtonSprite.size = this.closeButtonSize
            hover = this.hover = Some(TabClose)
            captured = this.captured = Some(TabClose)
        } :> ISprite

    member private this.pinButtonSprite =
        {
            PinButtonSprite.size = this.closeButtonSize
            hover = this.hover = Some(TabPin)
            captured = this.captured = Some(TabPin)
        } :> ISprite

    member private this.edgeWidth = 18

    member private this.renderTabEdge(path:GraphicsPath, startPoint:PointF, endPoint:PointF) =
        let width = endPoint.X - startPoint.X
        let height = endPoint.Y - startPoint.Y
        let xInc = width / float32(3)
        let xCurveInc = xInc / float32(3)
        let yCurveInc = height / float32(3)
        let bezPoints =
            [|
                startPoint
                PointF(startPoint.X + xInc, startPoint.Y)
                PointF(startPoint.X + xInc + xCurveInc, startPoint.Y + yCurveInc)
                PointF(startPoint.X + xInc + float32(2) * xCurveInc, startPoint.Y + float32(2) * yCurveInc)
                PointF(startPoint.X + float32(2) * xInc, startPoint.Y + float32(3) * yCurveInc)
                PointF(startPoint.X + float32(3) * xInc, startPoint.Y + float32(3) * yCurveInc)
            |]
        do path.AddBezier(
            bezPoints.[0],
            bezPoints.[1],
            bezPoints.[2],
            bezPoints.[3])
        do path.AddBezier(
            bezPoints.[2],
            bezPoints.[3],
            bezPoints.[4],
            bezPoints.[5])

    // Color resolution priority for non-active, non-flashing tabs:
    //   MouseOver (hover/capture)  >  Selected  >  Inactive
    // The active tab and the flash state win unconditionally over the rest.
    member private this.tabBgColor =
        match this.displayInfo.bgColor with
        | Some(color) -> color
        | None ->
            let active = this.appearance.tabActiveTabColor
            let inactive = this.appearance.tabInactiveTabColor
            let highlight = this.appearance.tabMouseOverTabColor
            let selected = this.appearance.tabSelectedTabColor
            if this.isTop then active
            elif this.hover.IsSome || this.captured.IsSome then highlight
            elif this.isSelected then selected
            else inactive

    member private this.bgBrush = SolidBrush(this.tabBgColor)

    member private this.borderPen =
        let color =
            match this.displayInfo.bgColor with
            | Some(_) -> this.appearance.tabFlashBorderColor
            | None ->
                let active = this.appearance.tabActiveBorderColor
                let inactive = this.appearance.tabInactiveBorderColor
                let highlight = this.appearance.tabMouseOverBorderColor
                let selected = this.appearance.tabSelectedBorderColor
                if this.isTop then active
                elif this.hover.IsSome || this.captured.IsSome then highlight
                elif this.isSelected then selected
                else inactive
        new Pen(new SolidBrush(color), 1.0f)

    member private this.borderPath =
        let path = new GraphicsPath()
        let bottom,top =
            match this.direction with
            | TabUp -> float32(this.size.height),float32(0)
            | TabDown -> float32(-1), float32(this.size.height - 1)
        do this.renderTabEdge(path, PointF(float32(0), bottom), PointF(float32(this.edgeWidth), top))
        do path.AddLine(Point(this.edgeWidth, int(top)), Point(this.size.width - this.edgeWidth, int(top)))
        do this.renderTabEdge(path, PointF(float32(this.size.width) - float32(this.edgeWidth), top), PointF(float32(this.size.width), bottom))
        path

    member private this.iconSize = 
        // Calculate icon size based on tab height, leaving some padding
        let iconHeight = max 16 (this.size.height - 8)
        let iconHeight = min iconHeight 24  // Cap at 24 pixels
        Sz(iconHeight, iconHeight)

    member private this.iconLocation =
        let y = (this.size.height - this.iconSize.height) / 2
        Pt(this.edgeWidth, y)

    member private this.closeButtonSize = Sz(18, 18)

    member private this.closeButtonLocation =
        let x = this.size.width - this.edgeWidth - this.closeButtonSize.width + 3
        let y = (this.size.height - this.closeButtonSize.height) / 2
        Pt(x, y)

    // Pin button location: same as close button but never overlaps with program icon
    member private this.pinButtonLocation =
        let normalX = this.closeButtonLocation.x
        let minX = this.iconLocation.x + this.iconSize.width + 2
        let x = max normalX minX
        let y = (this.size.height - this.closeButtonSize.height) / 2
        Pt(x, y)

    member this.textLocation =
        let x = this.iconLocation.x + this.iconSize.width + 5
        Pt(x, 0)

    member this.textSize =
        let width =
            if this.isPinned then
                // Pin button always visible: text area ends where pin button starts
                this.pinButtonLocation.x - this.textLocation.x
            elif this.hover.IsSome || this.captured.IsSome then
                // Hovered: text area ends where close button starts
                this.closeButtonLocation.x - this.textLocation.x
            else
                // Not hovered: extend text closer to right edge (fade handles visual boundary)
                this.size.width - this.textLocation.x - 18
        let width = max 0 width
        Sz(width, this.size.height)

    member this.tabTextBrush =
        let color =
            match this.displayInfo.bgColor with
            | Some(_) -> this.appearance.tabFlashTextColor  // Flash state uses flash text color
            | None ->
                let active = this.appearance.tabActiveTextColor
                let inactive = this.appearance.tabInactiveTextColor
                let highlight = this.appearance.tabMouseOverTextColor
                let selected = this.appearance.tabSelectedTextColor
                if this.isTop then active
                elif this.hover.IsSome || this.captured.IsSome then highlight
                elif this.isSelected then selected
                else inactive
        new SolidBrush(color)

    interface ISprite with
        member this.image =
            let img = Img(this.size)
            let g = img.graphics
            // Fill with near-transparent color so entire rectangular area is hit-testable
            // This prevents mouse events from passing through the gap between tab curves
            g.Clear(Color.FromArgb(1, 0, 0, 0))
            do g.FillPath(this.bgBrush, this.borderPath)
            // Draw fill color overlay (semi-transparent) between background and text
            match this.displayInfo.fillColor with
            | Some(fillColor) ->
                let state = g.Save()
                g.SetClip(this.borderPath)
                use fillBrush = new SolidBrush(fillColor)
                g.FillRectangle(fillBrush, Rectangle(0, 0, this.size.width, this.size.height))
                g.Restore(state)
            | None -> ()
            if this.onlyIcon.not && this.textSize.width > 0 then
                //the text can't be drawn as a separate bitmap because clearcase fonts
                //can't be drawn by gdi+ to a transparent background, need to draw directly on the tab background
                let text = this.displayInfo.text
                let font = this.displayInfo.textFont
                let brush = this.tabTextBrush
                let format = new StringFormat()
                do format.LineAlignment <- StringAlignment.Center
                do format.Alignment <- StringAlignment.Near
                do format.Trimming <- StringTrimming.None
                do format.FormatFlags <- format.FormatFlags ||| StringFormatFlags.NoWrap
                let bounds = Rect(this.textLocation, this.textSize)
                do g.DrawString(text, font, brush, bounds.Rectangle.RectangleF, format)
                // Draw fade-out gradient at right edge of text area (VSCode-style)
                let textMeasured = g.MeasureString(text, font)
                if textMeasured.Width > float32(bounds.size.width) then
                    let fadeWidth = min 15 this.textSize.width
                    if fadeWidth > 0 then
                        let fadeX = this.textLocation.x + this.textSize.width - fadeWidth
                        // Compute effective background color considering fill color overlay
                        let bgColor =
                            match this.displayInfo.fillColor with
                            | Some(fc) ->
                                let a = float fc.A / 255.0
                                let bg = this.tabBgColor
                                Color.FromArgb(255,
                                    int(float fc.R * a + float bg.R * (1.0 - a)),
                                    int(float fc.G * a + float bg.G * (1.0 - a)),
                                    int(float fc.B * a + float bg.B * (1.0 - a)))
                            | None -> this.tabBgColor
                        let fadeRect = Rectangle(fadeX, 0, fadeWidth + 1, this.size.height)
                        let state = g.Save()
                        g.SetClip(this.borderPath)
                        use fadeBrush = new LinearGradientBrush(
                            fadeRect,
                            Color.FromArgb(0, int bgColor.R, int bgColor.G, int bgColor.B),
                            bgColor,
                            LinearGradientMode.Horizontal)
                        g.FillRectangle(fadeBrush, fadeRect)
                        g.Restore(state)
            // Draw border after text and gradient so border is never affected by fade
            do g.DrawPath(this.borderPen, this.borderPath)
            // Draw underline color at bottom/top of tab with gradient (opaque left half, fading to transparent on right)
            match this.displayInfo.underlineColor with
            | Some(underlineColor) ->
                let underlineHeight = 3
                let state = g.Save()
                g.SetClip(this.borderPath)
                let y =
                    match this.direction with
                    | TabUp -> this.size.height - underlineHeight
                    | TabDown -> 0
                let rect = Rectangle(0, y, this.size.width, underlineHeight)
                let opaqueColor = underlineColor
                let transparentColor = Color.FromArgb(0, int underlineColor.R, int underlineColor.G, int underlineColor.B)
                use gradientBrush = new LinearGradientBrush(
                    Rectangle(0, y, this.size.width + 1, underlineHeight),
                    opaqueColor, transparentColor,
                    LinearGradientMode.Horizontal)
                // Opaque from left to 60%, then fade to 80% transparent on right
                let blend = new ColorBlend(3)
                blend.Positions <- [| 0.0f; 0.6f; 1.0f |]
                let rightColor = Color.FromArgb(int(255.0f * 0.2f), int underlineColor.R, int underlineColor.G, int underlineColor.B)
                blend.Colors <- [| opaqueColor; opaqueColor; rightColor |]
                gradientBrush.InterpolationColors <- blend
                g.FillRectangle(gradientBrush, rect)
                g.Restore(state)
            | None -> ()
            // Draw colored border around tab (normal width for tab shape, underline-style bottom edge)
            match this.displayInfo.borderColor with
            | Some(borderColor) ->
                let state = g.Save()
                // Draw the tab shape border (top and sides) with normal 1px width
                use shapePen = new Pen(new SolidBrush(borderColor), 1.0f)
                g.DrawPath(shapePen, this.borderPath)
                // Draw bottom/top edge using same logic as underline (SetClip + FillRectangle + gradient)
                let underlineHeight = 3
                g.SetClip(this.borderPath)
                let y =
                    match this.direction with
                    | TabUp -> this.size.height - underlineHeight
                    | TabDown -> 0
                let rect = Rectangle(0, y, this.size.width, underlineHeight)
                let opaqueColor = borderColor
                let rightColor = Color.FromArgb(int(255.0f * 0.2f), int borderColor.R, int borderColor.G, int borderColor.B)
                use gradientBrush = new LinearGradientBrush(
                    Rectangle(0, y, this.size.width + 1, underlineHeight),
                    opaqueColor, rightColor,
                    LinearGradientMode.Horizontal)
                let blend = new ColorBlend(3)
                blend.Positions <- [| 0.0f; 0.6f; 1.0f |]
                blend.Colors <- [| opaqueColor; opaqueColor; rightColor |]
                gradientBrush.InterpolationColors <- blend
                g.FillRectangle(gradientBrush, rect)
                g.Restore(state)
            | None -> ()
            img
        member this.children =
            let isHoverOrCaptured = this.hover.IsSome || this.captured.IsSome
            let iconRight = this.iconLocation.x + this.iconSize.width
            // Hide close button if it would overlap with program icon
            let canShowCloseButton = this.closeButtonLocation.x >= iconRight
            // Hide pin button if it would extend beyond tab right edge
            let canShowPinButton = this.pinButtonLocation.x + this.closeButtonSize.width <= this.size.width
            let showCloseButton = not this.onlyIcon && not this.isPinned && isHoverOrCaptured && canShowCloseButton
            let showPinButton = not this.onlyIcon && this.isPinned && canShowPinButton
            List2([
                Some(this.iconLocation, this.iconSprite)
                (if showCloseButton then Some(this.closeButtonLocation, this.closeButtonSprite) else None)
                (if showPinButton then Some(this.pinButtonLocation, this.pinButtonSprite) else None)
                ]).choose(id)

type TabStripSprite<'id> when 'id : equality = {
    tabs: Map2<'id, TabDisplayInfo>
    appearance: TabAppearanceInfo
    hover: ('id * TabPart) option
    captured : ('id * TabPart) option
    visualOrder: List2<'id>
    zorder: List2<'id>
    size: Sz
    slide: ('id * int) option
    tabAlignments: Map2<'id, TabAlign>
    direction: TabDirection
    transparent: bool
    pinnedTabs: Set2<'id>
    selectedTabs: Set2<'id>
    // Multi-tab drag group (variant A boundary): the pivot tab is
    // dragGroup.[0] and matches slide.tab. Other elements are dragged
    // along with the pivot, drawn at pivot.x + their relative offset.
    // Empty list = no group drag (= single-tab slide, existing behavior).
    dragGroup: 'id list
    } with

    member private this.tabOverlap = float(this.appearance.tabOverlap)
    member private this.unpinnedTabMaxLen = float(this.appearance.tabMaxWidth)
    member private this.pinnedTabMinLen =
        // Calculate dynamically based on tab height and icon size
        let tabHeight = this.size.height - 1
        let iconHeight = min (max 16 (tabHeight - 8)) 24
        float(18 + iconHeight + 18)  // left edge + icon width + right padding
    member private this.pinnedTabSettingLen = float(this.appearance.tabPinnedTabWidth)
    member private this.pinnedTabMaxLen =
        if this.appearance.tabPinnedTabWidthIcon then this.pinnedTabMinLen
        else max this.pinnedTabMinLen this.pinnedTabSettingLen

    member private this.count = this.visualOrder.length
    member private this.pinnedCount =
        this.visualOrder.where(fun tab -> this.pinnedTabs.contains(tab)).length
    member private this.unpinnedCount = this.count - this.pinnedCount

    member private this.isPinned (tab: 'id) = this.pinnedTabs.contains(tab)

    member private this.getTabAlign (tab: 'id) =
        match this.tabAlignments.tryFind(tab) with
        | Some(a) -> a
        | None -> TopLeft

    member private this.getAdjustedAlignment (tab: 'id) =
        match this.movedTab with
        | Some(t, _, alignment) when t = tab -> alignment
        // Group members follow the pivot's adjusted alignment.
        | Some(pivot, _, alignment) when this.isDragGroupMember tab && this.dragGroupPivot = Some pivot ->
            alignment
        | _ -> this.getTabAlign(tab)

    // --- Multi-tab drag group helpers ---
    // The pivot tab is whichever tab is being dragged (= slide.tab). It may
    // sit anywhere within dragGroup (e.g. dragGroup = [A; B; C] with B as
    // pivot). All offsets are computed relative to the pivot's position so
    // group members render at pivot.x + signed offset.
    member private this.dragGroupPivot : 'id option =
        match this.slide with
        | Some(t, _) -> Some t
        | None -> None

    member private this.isDragGroupMember (tab: 'id) =
        this.dragGroup |> List.contains tab

    // Signed offset of a group member from the pivot.
    // - Pivot itself: 0
    // - Members to the right of pivot in dragGroup: positive (Σ tabLen - overlap)
    // - Members to the left of pivot in dragGroup: negative (-Σ tabLen - overlap)
    // Uses Chrome-style tab overlap so the rendered group matches the
    // visual spacing of normal (non-dragged) tabs.
    member private this.dragGroupOffsetOf (tab: 'id) : float =
        match this.dragGroupPivot with
        | None -> 0.0
        | Some pivot ->
            let pIdx = this.dragGroup |> List.tryFindIndex ((=) pivot) |> Option.defaultValue 0
            let tIdx = this.dragGroup |> List.tryFindIndex ((=) tab) |> Option.defaultValue 0
            if tIdx = pIdx then 0.0
            elif tIdx > pIdx then
                // Right of pivot: accumulate positive step widths
                let mutable offset = 0.0
                for i in pIdx .. tIdx - 1 do
                    let t = this.dragGroup.[i]
                    let tLen = if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength
                    offset <- offset + tLen - this.tabOverlap
                offset
            else
                // Left of pivot: accumulate negative step widths
                let mutable offset = 0.0
                for i in tIdx .. pIdx - 1 do
                    let t = this.dragGroup.[i]
                    let tLen = if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength
                    offset <- offset - (tLen - this.tabOverlap)
                offset

    member private this.calcGroupWidth (tabs: List2<'id>) =
        if tabs.isEmpty then 0.0
        else
            let totalLen =
                tabs.list |> List.sumBy (fun t ->
                    if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength)
            totalLen - float(tabs.length - 1) * this.tabOverlap

    member private this.tabOffsetInGroup (tabs: List2<'id>) (tab: 'id) =
        let idx = tabs.list |> List.findIndex ((=) tab)
        tabs.list
        |> Seq.take idx
        |> Seq.fold (fun acc t ->
            let tLen = if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength
            acc + tLen - this.tabOverlap
        ) 0.0

    member private this.hitInGroup (tabs: List2<'id>) (groupStartX: float) (x: float) : Option<'id> =
        if tabs.isEmpty then None
        else
            let relX = x - groupStartX
            let tabList = tabs.list
            let mutable offset = 0.0
            let mutable result = tabList |> List.last
            let mutable found = false
            for i in 0 .. tabList.Length - 1 do
                if not found then
                    let t = tabList.[i]
                    let tLen = if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength
                    let boundary = offset + tLen - this.tabOverlap / 2.0
                    if relX < boundary || i = tabList.Length - 1 then
                        result <- t
                        found <- true
                    offset <- offset + tLen - this.tabOverlap
            Some(result)

    // Pinned tab width: fixed at pinnedTabMaxLen unless space is too tight
    member private this.pinnedTabLength =
        if this.pinnedCount = 0 then this.pinnedTabMaxLen
        else
            let totalOverlap = float(max 0 (this.count - 1)) * this.tabOverlap
            let availableWidth = float(this.size.width) + totalOverlap
            let pinnedMaxTotal = float(this.pinnedCount) * this.pinnedTabMaxLen
            let unpinnedRemaining = availableWidth - pinnedMaxTotal
            // If unpinned tabs still have at least 1px each, keep pinned at max
            if this.unpinnedCount = 0 || unpinnedRemaining / float(this.unpinnedCount) >= 1.0 then
                this.pinnedTabMaxLen
            else
                // Everything too tight, use uniform width
                availableWidth / float(this.count)

    // Unpinned tab width: fills remaining space after pinned tabs
    member private this.unpinnedTabLength =
        if this.unpinnedCount = 0 then this.unpinnedTabMaxLen
        else
            let totalOverlap = float(max 0 (this.count - 1)) * this.tabOverlap
            let availableWidth = float(this.size.width) + totalOverlap
            let pinnedTotal = float(this.pinnedCount) * this.pinnedTabLength
            let unpinnedAvailable = availableWidth - pinnedTotal
            min (unpinnedAvailable / float(this.unpinnedCount)) this.unpinnedTabMaxLen

    member private this.tabSprite (tab:'id) =
        let isPinned = this.isPinned(tab)
        let tabLen = if isPinned then this.pinnedTabLength else this.unpinnedTabLength
        {
            TabSprite.id = tab
            isTop =
                match this.zorder.tryHead with
                | Some(top) -> top = tab
                | None -> false
            isSelected = this.selectedTabs.contains(tab)
            displayInfo = this.tabs.find(tab)
            appearance =
                if isPinned then
                    { this.appearance with tabMaxWidth = int(this.pinnedTabLength) }
                else
                    this.appearance
            size = Sz(int(tabLen), (this.size.height) - 1)
            onlyIcon = isPinned && this.appearance.tabPinnedTabWidthIcon
            isPinned = isPinned
            direction = this.direction
            hover =
                match this.hover with
                | Some(id, part) when id = tab -> Some(part)
                | _ -> None
            captured =
                match this.captured with
                | Some(id, part) when id = tab -> Some(part)
                | _ -> None
        } :> ISprite

    member private this.bgImage =
        let bgColor =
            if this.transparent then Color.FromArgb(0, 0, 0, 0)
            else Color.FromArgb(1, 1, 1, 1)
        let gr, img =
            let sz = this.size
            if sz.isEmptyArea then
                let bmp = new Bitmap(1,1)
                let gr = Graphics.FromImage(bmp)
                do gr.SmoothingMode <- SmoothingMode.AntiAlias
                (gr, bmp)
            else
                let bmp = new Bitmap(sz.width, sz.height)
                let gr = Graphics.FromImage(bmp)
                do gr.SmoothingMode <- SmoothingMode.AntiAlias
                (gr, bmp)
        let bounds = Rect(Pt(), this.size)
        do  gr.FillRectangle(new SolidBrush(bgColor), bounds.Rectangle)
        img.img

    member private this.tabYOffset =
        match this.direction with
        | TabUp -> 0
        | TabDown -> 1

    member this.tabLocation tab =
        match this.slide with
        | Some(slideTab, x) when tab = slideTab ->
            // Pivot tab: render at drag x, clamped to its own single-tab
            // width. Group members may extend beyond the strip edge — that
            // is the natural consequence of dragging a group near an edge
            // and the user can drag back.
            let tabLen = if this.isPinned(tab) then this.pinnedTabLength else this.unpinnedTabLength
            let bounds = (0, this.size.width - int(tabLen))
            Pt(between bounds x, this.tabYOffset)
        | Some(pivotTab, pivotX) when tab <> pivotTab && this.isDragGroupMember tab ->
            // Group member: pivot.x + signed offset (left = negative,
            // right = positive). Use the same single-tab clamp as the
            // pivot so the group members track the visible pivot.
            let pivotLen = if this.isPinned(pivotTab) then this.pinnedTabLength else this.unpinnedTabLength
            let pivotBounds = (0, this.size.width - int(pivotLen))
            let clampedPivot = between pivotBounds pivotX
            let offset = this.dragGroupOffsetOf tab
            Pt(clampedPivot + int(offset), this.tabYOffset)
        | _ ->
            let adjusted = this.adjustedVisualOrder
            let tabAlignment = this.getAdjustedAlignment(tab)
            let groupTabs = adjusted.where(fun t -> this.getAdjustedAlignment(t) = tabAlignment)
            let groupStartX =
                match tabAlignment with
                | TopLeft -> 0.0
                | TopRight ->
                    let groupWidth = this.calcGroupWidth(groupTabs)
                    max 0.0 (float(this.size.width) - groupWidth)
            let offset = this.tabOffsetInGroup groupTabs tab
            Pt(int(groupStartX + offset), this.tabYOffset)

    member this.tabSize = Sz(int(this.unpinnedTabLength), (this.size.height) - 1)

    member this.movedTab : ('id * int * TabAlign) option =
        match this.slide with
        | Some(tab, x) ->
            if this.count = 0 then Some(tab, 0, TopLeft)
            else
                // Group-drag detection: judge by the GROUP HEAD's center
                // (= dragGroup.[0]), not the pivot's. This keeps the drop
                // index stable regardless of which group member the user
                // grabbed: a stationary [C,D,E] should give the same
                // visualIdx whether the user pivots on C or D.
                //
                // Concretely, pivot.x is the mouse x; the head sits at
                // pivot.x + dragGroupOffsetOf(head) (negative if head is
                // left of pivot). The center used for boundary tests is
                // the head's center.
                let groupTabs =
                    if List.isEmpty this.dragGroup then [tab] else this.dragGroup
                let inGroup t = List.contains t groupTabs
                let groupHead =
                    if List.isEmpty this.dragGroup then tab else List.head this.dragGroup
                let dragTabLen =
                    if this.isPinned(groupHead) then this.pinnedTabLength else this.unpinnedTabLength
                let groupHeadX = float(x) + this.dragGroupOffsetOf groupHead
                let centerX = groupHeadX + dragTabLen / 2.0

                // Calculate group tabs and widths (exclude every tab in the
                // drag group, not just the pivot).
                let leftWithout = this.visualOrder.where(fun t -> not (inGroup t) && this.getTabAlign(t) = TopLeft).list
                let rightWithout = this.visualOrder.where(fun t -> not (inGroup t) && this.getTabAlign(t) = TopRight).list
                let calcWidth (tabs: 'id list) =
                    if tabs.IsEmpty then 0.0
                    else
                        let total = tabs |> List.sumBy (fun t ->
                            if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength)
                        total - float(tabs.Length - 1) * this.tabOverlap
                let leftWidthWithout = calcWidth leftWithout
                let rightWidthWithout = calcWidth rightWithout

                // For alignment detection, use widths without the dragged tab(s)
                let rightStartXWithout = float(this.size.width) - rightWidthWithout
                let emptyCenterWithout = (leftWidthWithout + rightStartXWithout) / 2.0

                // Determine target alignment
                let targetAlignment = if centerX >= emptyCenterWithout then TopRight else TopLeft

                // For position calculation within the group, include the dragged blob's
                // width in the target alignment group to match how tabLocation renders
                let leftWidthFull =
                    if targetAlignment = TopLeft then
                        let overlap = if leftWithout.IsEmpty then 0.0 else this.tabOverlap
                        leftWidthWithout + dragTabLen - overlap
                    else leftWidthWithout
                let rightWidthFull =
                    if targetAlignment = TopRight then
                        let overlap = if rightWithout.IsEmpty then 0.0 else this.tabOverlap
                        rightWidthWithout + dragTabLen - overlap
                    else rightWidthWithout

                // Calculate target index within the alignment group
                let (groupList, groupStartX) =
                    match targetAlignment with
                    | TopLeft -> (leftWithout, 0.0)
                    | TopRight ->
                        let startX = float(this.size.width) - rightWidthFull
                        (rightWithout, startX)

                // Hybrid swap-judge basis (variant S / -19):
                //   - Right-aligned + multi-tab drag: group CENTER
                //     (variant R / -18 behaved well for this case because
                //      the user reads the whole moving block, not its
                //      left-aligned head).
                //   - All other cases (single tab, or left-aligned drag):
                //     group HEAD's LEFT EDGE (variant Q / -17 felt right
                //      because the leftmost moving tab is the visual
                //      anchor in those cases).
                let isRightMulti =
                    targetAlignment = TopRight && List.length groupTabs > 1
                let groupTotalWidth =
                    let n = List.length groupTabs
                    let total =
                        groupTabs |> List.sumBy (fun t ->
                            if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength)
                    total - float(max 0 (n - 1)) * this.tabOverlap
                let judgeX =
                    if isRightMulti then groupHeadX + groupTotalWidth / 2.0
                    else groupHeadX
                let groupIndex =
                    if groupList.IsEmpty then 0
                    else
                        let mutable idx = groupList.Length
                        let mutable offset = 0.0
                        let mutable found = false
                        for i in 0 .. groupList.Length - 1 do
                            if not found then
                                let t = groupList.[i]
                                let tLen = if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength
                                // Swap when judgeX crosses t's CENTER.
                                // judgeX is the variant-specific basis
                                // (group center for right-multi, group
                                // head's left edge otherwise).
                                let tCenter = groupStartX + offset + tLen / 2.0
                                if judgeX < tCenter then
                                    idx <- i
                                    found <- true
                                offset <- offset + (tLen - this.tabOverlap)
                        max 0 (min idx groupList.Length)

                // Convert group index to visual order index for splicing.
                // visualWithout = visualOrder - the whole drag group.
                let visualWithout = this.visualOrder.where(fun t -> not (inGroup t)).list
                let mutable groupCount = 0
                let mutable visualIdx = visualWithout.Length
                let mutable found2 = false
                for i in 0 .. visualWithout.Length - 1 do
                    if not found2 then
                        let t = visualWithout.[i]
                        if this.getTabAlign(t) = targetAlignment then
                            if groupCount = groupIndex then
                                visualIdx <- i
                                found2 <- true
                            else
                                groupCount <- groupCount + 1
                                visualIdx <- i + 1

                // visualIdx is where the pivot's center landed in
                // visualWithout. Use it directly as the splice index for
                // the whole group: the group's first member lands there
                // and the pivot ends up at spliceIdx + pivotIdxInGroup.
                //
                // The previous "spliceIdx = visualIdx - pivotIdxInGroup"
                // shift made it impossible to splice the group at the very
                // end of visualWithout (the pivot's max visualIdx equals
                // visualWithout.Length, and subtracting pivotIdxInGroup
                // pulled the group back from the tail). Without the shift,
                // the group correctly reaches the tail when the user drags
                // the pivot past the last non-group tab.
                let spliceIdx = max 0 (min visualWithout.Length visualIdx)

                Some(tab, spliceIdx, targetAlignment)
        | None -> None

    member this.adjustedVisualOrder : List2<'id> =
        match this.movedTab with
        | Some(tab, index, _) ->
            if List.isEmpty this.dragGroup then
                this.visualOrder.move((=)tab, index)
            else
                // Splice the entire drag group at `index` of visualOrder-without-group
                let grp = this.dragGroup
                let cur = this.visualOrder.list
                let withoutGroup = cur |> List.filter (fun t -> not (List.contains t grp))
                let safeIndex = max 0 (min withoutGroup.Length index)
                let newOrder =
                    (withoutGroup |> List.truncate safeIndex)
                    @ grp
                    @ (withoutGroup |> List.skip safeIndex)
                List2(newOrder)
        | None -> this.visualOrder


    member this.sprite =
        {
            new ISprite with
            member x.image = this.bgImage
            member x.children =
                // Z-order: when a multi-tab drag group is active, bring its
                // members to the front (end of children = topmost),
                // ordered by visualOrder so left→right Chrome-style overlap
                // is preserved within the group. Without this, a group
                // member that sits behind the pivot in z-order would get
                // covered by the pivot's overlap notch, producing the
                // "[---A---]-B---]" artifact.
                let order =
                    if List.isEmpty this.dragGroup then
                        this.zorder
                    else
                        let grp = this.dragGroup
                        let nonGroup = this.zorder.where(fun t -> not (List.contains t grp))
                        let groupOrdered = this.visualOrder.where(fun t -> List.contains t grp)
                        nonGroup.appendList(groupOrdered)
                order.map <| fun (tab:'id) ->
                    (this.tabLocation tab, this.tabSprite(tab))
        }

    member this.renderTab tab = this.tabSprite(tab).render

    member this.render = this.sprite.render

    member this.tryHit pt =
        // Determine the owning tab purely geometrically (boundary at the
        // midpoint of each tab overlap, independent of z-order) so a tab that
        // is drawn on top never steals the exposed area — including the
        // Close/Pin button — of the tab overlapped behind it. This uses the
        // same partition as the tooltip hit test: every tab gets a constant
        // hover width regardless of which tab is frontmost.
        maybe {
            let! tab = this.tryHitForTooltip pt
            // Resolve the part (Close/Pin/Icon/Background) within that tab by
            // hit-testing the tab's own sprite at tab-local coordinates.
            let path = this.tabSprite(tab).hit(pt.sub(this.tabLocation tab))
            let part : TabPart =
                path.tryPick (fun sprite ->
                    match sprite with
                    | :? CloseButtonSprite -> Some(TabClose)
                    | :? PinButtonSprite -> Some(TabPin)
                    | :? IconSprite -> Some(TabIcon)
                    | _ -> None)
                |> Option.defaultValue TabBackground
            return tab,part
        }

    // Tab-ownership hit test (used by both tooltip and tryHit): boundary at the
    // midpoint of each tab overlap, ignoring z-order, so every tab gets a
    // constant hover width. The LAST tab of a group has no right neighbour, so
    // left unadjusted it would own the full trapezoid down to its slanted
    // bottom edge — half an overlap wider than the interior tabs. Trim each
    // group's right limit by overlap/2 so the last tab's hover width matches
    // the others (treating tabs as equal-width rectangles, ignoring the slanted
    // bottom edge). The close button stays well inside the trimmed limit.
    member this.tryHitForTooltip (pt: Pt) : Option<'id> =
        if this.count = 0 then None
        else
            let yOff = this.tabYOffset
            let tabH = (this.size.height) - 1
            if pt.y >= yOff && pt.y < yOff + tabH then
                let x = float(pt.x)
                let adjusted = this.adjustedVisualOrder
                let leftTabs = adjusted.where(fun t -> this.getAdjustedAlignment(t) = TopLeft)
                let rightTabs = adjusted.where(fun t -> this.getAdjustedAlignment(t) = TopRight)
                let leftWidth = this.calcGroupWidth(leftTabs)
                let rightWidth = this.calcGroupWidth(rightTabs)
                let rightStartX = float(this.size.width) - rightWidth
                // Trim the last tab's trapezoid-bottom extension (only meaningful
                // when tabs actually overlap).
                let trim = max 0.0 (this.tabOverlap / 2.0)
                if x < leftWidth - trim && not leftTabs.isEmpty then
                    this.hitInGroup leftTabs 0.0 x
                elif x >= rightStartX && x < float(this.size.width) - trim && not rightTabs.isEmpty then
                    this.hitInGroup rightTabs rightStartX x
                else
                    None
            else
                None
