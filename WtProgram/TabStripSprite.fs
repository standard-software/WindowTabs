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

    member private this.tabBgColor =
        match this.displayInfo.bgColor with
        | Some(color) -> color
        | None ->
            let active = this.appearance.tabActiveTabColor
            let inactive = this.appearance.tabInactiveTabColor
            let highlight = this.appearance.tabMouseOverTabColor
            if this.isTop then active
            elif this.hover.IsSome || this.captured.IsSome then highlight
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
                if this.isTop then active
                elif this.hover.IsSome || this.captured.IsSome then highlight
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
                if this.isTop then active
                elif this.hover.IsSome || this.captured.IsSome then highlight
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
    lorder: List2<'id>
    zorder: List2<'id>
    size: Sz
    slide: ('id * int) option
    alignment: Bemo.TabAlignment
    direction: TabDirection
    transparent: bool
    pinnedTabs: Set2<'id>
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

    member private this.count = this.lorder.length
    member private this.pinnedCount =
        this.lorder.where(fun tab -> this.pinnedTabs.contains(tab)).length
    member private this.unpinnedCount = this.count - this.pinnedCount

    member private this.isPinned (tab: 'id) = this.pinnedTabs.contains(tab)

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

    member private this.tabLengthFor index =
        if index < this.pinnedCount then this.pinnedTabLength
        else this.unpinnedTabLength

    member private this.tabSprite (tab:'id) =
        let isPinned = this.isPinned(tab)
        let tabLen = if isPinned then this.pinnedTabLength else this.unpinnedTabLength
        {
            TabSprite.id = tab
            isTop =
                match this.zorder.tryHead with
                | Some(top) -> top = tab
                | None -> false
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

    // Tab offset: accounts for different widths of pinned vs unpinned tabs
    member private this.tabOffset index =
        if index <= 0 then 0.0
        elif index <= this.pinnedCount then
            float(index) * (this.pinnedTabLength - this.tabOverlap)
        else
            let pinnedOffset = float(this.pinnedCount) * (this.pinnedTabLength - this.tabOverlap)
            let unpinnedLocalIndex = index - this.pinnedCount
            pinnedOffset + float(unpinnedLocalIndex) * (this.unpinnedTabLength - this.tabOverlap)

    member private this.alignmentOffset =
        if this.count = 0 then 0.0
        else
            let lastIndex = this.count - 1
            let lastTabLen = this.tabLengthFor lastIndex
            let lastTabRight = this.tabOffset lastIndex + lastTabLen
            let widthOfEmptySpace = float(this.size.width) - lastTabRight
            match this.alignment with
            | TabLeft -> 0.0
            | TabRight -> widthOfEmptySpace

    member private this.tabYOffset =
        match this.direction with
        | TabUp -> 0
        | TabDown -> 1

    member this.tabLocation tab =
        match this.slide with
        | Some(slideTab, x) when tab = slideTab ->
            let tabLen = if this.isPinned(tab) then this.pinnedTabLength else this.unpinnedTabLength
            let bounds = (0, this.size.width - int(tabLen))
            Pt(between bounds x, this.tabYOffset)
        | _ ->
            // Compute offset by iterating through adjustedLorder with per-tab widths
            // This correctly handles cross-zone drag where tab order differs from pinned zones
            let adjusted = this.adjustedLorder
            let tabIdx = adjusted.findIndex((=)tab)
            let offset =
                adjusted.list
                |> Seq.take tabIdx
                |> Seq.fold (fun acc t ->
                    let tLen = if this.isPinned(t) then this.pinnedTabLength else this.unpinnedTabLength
                    acc + tLen - this.tabOverlap
                ) 0.0
            let x = offset + this.alignmentOffset
            Pt(int(x), this.tabYOffset)

    member this.tabSize = Sz(int(this.unpinnedTabLength), (this.size.height) - 1)

    member this.movedTab =
        match this.slide with
        | Some(tab, x) ->
            let index =
                if this.count = 0 then 0
                else
                    let dragTabLen = if this.isPinned(tab) then this.pinnedTabLength else this.unpinnedTabLength
                    let x = float(x) - this.alignmentOffset
                    let centerX = x + dragTabLen / 2.0
                    // Allow cross-zone drag (VSCode-style): determine target index
                    // based on which zone the center of the dragged tab falls in
                    let pinnedStep = this.pinnedTabLength - this.tabOverlap
                    let pinnedZoneEnd = float(this.pinnedCount) * pinnedStep
                    if this.pinnedCount > 0 && centerX < pinnedZoneEnd then
                        // Center is in pinned zone
                        if pinnedStep <= 0.0 then 0
                        else
                            let idx = int(centerX / pinnedStep)
                            max 0 (min idx (this.count - 1))
                    else
                        // Center is in unpinned zone
                        let unpinnedStep = this.unpinnedTabLength - this.tabOverlap
                        if unpinnedStep <= 0.0 then this.pinnedCount
                        else
                            let relX = centerX - pinnedZoneEnd
                            let relIdx = int(relX / unpinnedStep)
                            let idx = this.pinnedCount + relIdx
                            max 0 (min idx (this.count - 1))
            Some(tab, index)
        | None -> None

    member this.adjustedLorder : List2<'id> =
        match this.movedTab with
        | Some(tab, index) -> this.lorder.move((=)tab, index)
        | None -> this.lorder


    member this.sprite =
        {
            new ISprite with
            member x.image = this.bgImage
            member x.children = this.zorder.map <| fun (tab:'id) ->
                (this.tabLocation tab, this.tabSprite(tab))
        }

    member this.renderTab tab = this.tabSprite(tab).render

    member this.render = this.sprite.render

    member this.tryHit pt =
        let path = this.sprite.hit(pt)
        maybe {
            let! tab = path.tryPick <| fun sprite ->
                match sprite with
                | :? TabSprite<'id> as ts -> Some(ts.id)
                | _ -> None
            let part : TabPart =
                match path.head with
                | :? TabSprite<'id> -> TabBackground
                | :? IconSprite -> TabIcon
                | :? CloseButtonSprite -> TabClose
                | :? PinButtonSprite -> TabPin
                | _ -> TabBackground
            return tab,part
        }

    // Tooltip hit test: boundary at midpoint of tab overlap area, ignoring z-order
    member this.tryHitForTooltip (pt: Pt) : Option<'id> =
        if this.count = 0 then None
        else
            let yOff = this.tabYOffset
            let tabH = (this.size.height) - 1
            if pt.y >= yOff && pt.y < yOff + tabH then
                let relX = float(pt.x) - this.alignmentOffset
                // Determine which zone the point falls in
                let pinnedZoneEnd =
                    if this.pinnedCount = 0 then 0.0
                    else
                        this.tabOffset this.pinnedCount
                if relX < pinnedZoneEnd && this.pinnedCount > 0 then
                    // In pinned zone
                    let step = this.pinnedTabLength - this.tabOverlap
                    if step <= 0.0 then Some(this.adjustedLorder.head)
                    else
                        let index = int(floor((relX - this.tabOverlap / 2.0) / step))
                        let index = max 0 (min index (this.pinnedCount - 1))
                        Some(this.adjustedLorder.skip(index).head)
                else
                    // In unpinned zone
                    if this.unpinnedCount = 0 then None
                    else
                        let unpinnedStart = float(this.pinnedCount) * (this.pinnedTabLength - this.tabOverlap)
                        let unpinnedRelX = relX - unpinnedStart
                        let step = this.unpinnedTabLength - this.tabOverlap
                        if step <= 0.0 then Some(this.adjustedLorder.skip(this.pinnedCount).head)
                        else
                            let index = int(floor((unpinnedRelX - this.tabOverlap / 2.0) / step))
                            let index = max 0 (min index (this.unpinnedCount - 1))
                            Some(this.adjustedLorder.skip(this.pinnedCount + index).head)
            else
                None
