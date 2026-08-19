namespace Bemo
open System
open System.Drawing
open System.Windows.Forms

// WinForms enumerates Screen.AllScreens once and caches it for the process
// lifetime. Two things make that cache wrong on this desktop:
//
//  * a display configuration change (monitor on/off, resolution change) leaves
//    it stale, or - when the re-enumeration races the transition - filled with
//    zero-bounds entries (GetMonitorInfo fails on dying HMONITORs);
//  * the entries are enumerated in the coordinate space of whichever thread
//    filled them. WindowTabs is Per-Monitor-V2 aware throughout; what is left
//    of the DPI-unaware island is the workspace placement pair and the font
//    probe (see Dpi.withUnawareContext), and anything that fills the cache
//    from inside one of those fills it with virtualized rectangles instead of
//    device pixels.
//
// WindowTabs' OWN code no longer depends on this. Monitor geometry comes from
// live MonitorFromPoint / MonitorFromRect / EnumDisplayMonitors queries (Mon,
// MonitorScreen, the Dpi functions below), which are correct by construction
// and cannot go stale. What remains is the Screen cache that WinForms uses
// INTERNALLY - MessageBox placement, FormStartPosition.CenterScreen, tooltip
// clamping - and that WindowTabs cannot route around.
//
// Clearing the private cache field forces the next access to re-enumerate in
// the caller's own coordinate space. A full enumeration costs microseconds, so
// it is done on both edges of the unaware island.
//
// If a future runtime renames the field, `screensField` is null and this
// becomes a no-op. Nothing WindowTabs draws or hit-tests changes; the worst
// case is a WinForms-positioned dialog (the "language changed" message box)
// appearing on the monitor the settings window last enumerated rather than the
// intended one. That is a deliberate downgrade from the second generation,
// where the same failure would have moved windows to the wrong display.
module ScreenCache =
    let private screensField =
        try typeof<Screen>.GetField("screens", Reflection.BindingFlags.Static ||| Reflection.BindingFlags.NonPublic)
        with _ -> null

    let refresh() =
        try
            if screensField <> null then screensField.SetValue(null, null)
        with _ -> ()

// DPI scaling for everything WindowTabs draws itself.
//
// The process is Per-Monitor-V2 DPI aware (see WtProgram/app.manifest), so
// every Win32 coordinate the app sees or sets is a REAL device pixel and the
// tab strip's layered bitmap is presented 1:1 - no OS bitmap stretch, no blur.
// The flip side is that nothing scales automatically any more: a 25 px tab
// strip would be 25 physical px (two thirds of its former size) on a 150%
// monitor. Every self-drawn size therefore has to be multiplied by the scale
// of the monitor the strip is on, which is what this module provides.
//
// Scale is always derived per monitor (GetDpiForMonitor / MDT_EFFECTIVE_DPI),
// never from the system DPI, so a 150% monitor and a 100% monitor on the same
// desktop each get their own factor and the result does not depend on which
// monitor happens to be primary.
module Dpi =

    [<Literal>]
    let BaseDpi = 96.0

    /// Scale factor of a monitor handle (1.0 = 100%, 1.5 = 150%).
    let scaleForMonitorHandle (hMonitor: IntPtr) =
        float (DpiApi.GetDpiForMonitorHandle(hMonitor)) / BaseDpi

    /// Scale factor of the monitor containing (or nearest to) a rectangle.
    /// MonitorFromRect picks the monitor with the largest intersection, which
    /// is the rule the whole app uses at a monitor boundary.
    let scaleForRect (r: Rect) =
        float (DpiApi.GetDpiForRect(r.x, r.y, max 1 r.width, max 1 r.height)) / BaseDpi

    /// Scale factor of the monitor containing (or nearest to) a point.
    let scaleForPoint (pt: Pt) =
        float (DpiApi.GetDpiForPoint(pt.x, pt.y)) / BaseDpi

    /// Scale factor of the monitor a window is on.
    let scaleForHwnd (hwnd: IntPtr) =
        match Mon.fromHwnd(hwnd) with
        | Some(mon) -> scaleForMonitorHandle mon.hMonitor
        | None -> 1.0

    /// Scale factor for the tab strip that decorates `windowRect`.
    ///
    /// The strip is a wide, short band anchored on the TOP EDGE of the tracked
    /// window, so the monitor is chosen from that edge line rather than from
    /// the whole window rectangle: a tall window whose lower half sits on
    /// another monitor must not drag the strip's scale away from the monitor
    /// the strip is actually displayed on.
    ///
    /// The probe deliberately does NOT include the strip's own height. The
    /// height is a *result* of the scale, so feeding it back in would make the
    /// query circular and let the factor oscillate at a boundary. Because the
    /// probe depends only on the window rectangle, the switch point is the
    /// same in both directions and dragging a window back and forth across a
    /// boundary always reproduces the same two values.
    let scaleForStripAnchor (windowRect: Rect) =
        scaleForRect (Rect(Pt(windowRect.x, windowRect.y), Sz(max 1 windowRect.width, 1)))

    /// Scale an integer design size (96-dpi units) to device pixels.
    /// Halves round away from zero - Windows' own convention, and symmetric
    /// for the negative values used by per-exe window margins. The default
    /// banker's rounding would turn 3 * 1.5 into 4 instead of 5, and a
    /// "max 1" style clamp would turn a deliberate 0 (e.g. Tab Overlap = 0)
    /// into 1 even at 100%. Zero stays zero and negatives stay negative here.
    let px (scale: float) (value: int) =
        if scale = 1.0 then value
        else int (Math.Round(float value * scale, MidpointRounding.AwayFromZero))

    /// Scale a float32 design size (pen widths, glyph offsets). No rounding:
    /// GDI+ draws these at sub-pixel precision.
    let pxf (scale: float) (value: float32) =
        if scale = 1.0 then value
        else float32 (float value * scale)

    // Fonts are cached because the strip rebuilds its whole sprite tree on
    // every repaint; without the cache each repaint would allocate one Font
    // per tab and leave a GDI handle waiting for the finalizer.
    //
    // The cache is per THREAD: every tab group renders on its own thread and
    // System.Drawing objects are explicitly not guaranteed to be thread safe,
    // so a Font instance must not be measured and drawn with concurrently.
    // A thread's dictionary becomes unreachable when the group thread exits.
    //
    // Bounded, because the keys are unbounded in principle (any family, any
    // monitor scale). In practice a group thread sees two families, two or
    // three scales and one style, so the limit is never approached; it exists
    // so a pathological configuration cannot accumulate GDI handles forever.
    [<Literal>]
    let private fontCacheLimit = 32

    let private fontCache =
        new Threading.ThreadLocal<Collections.Generic.Dictionary<string * float32 * FontStyle, Font>>(
            fun () -> Collections.Generic.Dictionary<string * float32 * FontStyle, Font>())

    // Fonts evicted from the cache when it overflows. They are NOT disposed:
    // fonts handed out by makeFont are assigned to live controls (Form.Font,
    // ToolStrip.Font, the TreeViewAdv fonts), and WinForms keeps using such a
    // Font long after the assignment - disposing an evicted one tore the HFONT
    // out from under whatever control still held it and left GDI+ to throw or
    // draw nothing on the next repaint. Retiring them here keeps them alive
    // for the rest of the process; an overflow is a once-per-pathological-
    // configuration event, so this holds a few dozen small objects at worst.
    let private retiredFonts =
        new Threading.ThreadLocal<Collections.Generic.List<Font>>(
            fun () -> Collections.Generic.List<Font>())

    let private makeFont (family: string, pixelSize: float32, style: FontStyle) =
        // GraphicsUnit.Pixel, not Point. GDI+ converts a point size with
        // size * dpi / 72 using the DPI of the DESTINATION surface, and
        // `new Bitmap()` inherits the process's DPI awareness - in a
        // Per-Monitor-V2 process that is the system DPI (144 on a 150%
        // primary), so a 9 pt font would silently come out 1.5x too large and
        // a strip that also multiplied by the monitor scale would end up at
        // 2.25x. A pixel-sized font is immune to the destination's DPI, so the
        // on-screen text size is decided purely by `scale` here, and the strip
        // (memory bitmap), the tooltip and the rename box (screen DCs) all
        // agree. See also Img, which pins its bitmaps to 96 dpi.
        let cache = fontCache.Value
        let key = (family, pixelSize, style)
        match cache.TryGetValue(key) with
        | true, font -> font
        | _ ->
            if cache.Count >= fontCacheLimit then
                // See retiredFonts: never Dispose - a cached Font may still be
                // in use as some control's Font.
                for kv in cache do
                    retiredFonts.Value.Add(kv.Value)
                cache.Clear()
            let font = new Font(family, pixelSize, style, GraphicsUnit.Pixel)
            cache.[key] <- font
            font

    /// Pixel size a point-sized font occupies at 96 dpi.
    let private pixelSizeOf (font: Font) = font.SizeInPoints * 96.0f / 72.0f

    /// A pixel-sized copy of `font`, enlarged by `scale`. At scale = 1.0 this
    /// renders exactly like the original point-sized font on a 96-dpi surface
    /// (SystemFonts.MenuFont is 9 pt = 12 px), so a 100% monitor is unchanged.
    let scaledFont (font: Font) (scale: float) =
        try makeFont(font.FontFamily.Name, pxf scale (pixelSizeOf font), font.Style)
        with _ -> font

    /// A pixel-sized font of the given family, or None when the family is not
    /// installed (used for the Segoe MDL2 Assets pin glyph).
    let scaledFontFamily (family: string) (basePixelSize: float32) (style: FontStyle) (scale: float) =
        try
            let f = makeFont(family, pxf scale basePixelSize, style)
            if f.FontFamily.Name = family then Some(f) else None
        with _ -> None

    /// Scale the layout (non-colour) fields of a tab appearance. Colours and
    /// the "pinned tab shows icon only" flag are DPI-independent.
    let scaleAppearance (scale: float) (a: TabAppearanceInfo) =
        if scale = 1.0 then a
        else
            { a with
                tabHeight = px scale a.tabHeight
                tabMaxWidth = px scale a.tabMaxWidth
                tabPinnedTabWidth = px scale a.tabPinnedTabWidth
                tabOverlap = px scale a.tabOverlap
                tabHeightOffset = px scale a.tabHeightOffset
                tabIndentFlipped = px scale a.tabIndentFlipped
                tabIndentNormal = px scale a.tabIndentNormal }

    /// Opt the process into Per-Monitor-V2 awareness at runtime. The manifest
    /// normally did this already (then this call is a no-op); it exists so the
    /// fix survives a build that loses the win32 manifest.
    let enablePerMonitorV2() =
        try DpiApi.EnablePerMonitorV2() |> ignore
        with _ -> ()

    /// Run `f` with the calling thread switched to DPI-unaware, and restore
    /// the previous context afterwards. Window-rectangle APIs answer in the
    /// coordinate space of the calling thread, and Windows hands an unaware
    /// thread 96-dpi metrics, so this is how a call is made to return - or
    /// accept - the numbers it used to see.
    ///
    /// Three callers remain. WorkspaceModel wraps GetWindowPlacement and
    /// SetWindowPlacement so the rectangles persisted in settings.json keep
    /// meaning what earlier versions wrote. legacyDialogFont below reads the
    /// 96-dpi stock UI font. Program.fs pins the one process-wide DPI
    /// snapshot WinForms takes to 96. The settings dialog itself is no longer
    /// built in here - see SettingsDpi.
    ///
    /// The WinForms screen cache is dropped on both edges: whatever `f` fills
    /// it with is virtualized, and whatever it held before is not what `f`
    /// expects to see.
    let withUnawareContext (f: unit -> 'a) : 'a =
        let previous = DpiApi.SetThreadUnaware()
        ScreenCache.refresh()
        try f()
        finally
            DpiApi.RestoreThreadContext(previous)
            ScreenCache.refresh()

    // The UI font the settings dialog was designed against.
    //
    // Declaring DPI awareness in the manifest made Windows hand the process
    // scaled metrics, and .NET passes that on: on a 125% desktop every default
    // font source - Control.DefaultFont, SystemFonts.DefaultFont, the
    // DEFAULT_GUI_FONT stock object - reports 11.25pt where an unaware process
    // sees 9pt. The dialog's layout is hard-coded in 96-dpi pixels (a 250-px
    // label column, 35-px rows), so the 25% larger text overflowed captions
    // that used to fit: they wrapped onto a second line, which the fixed row
    // height then cut off.
    //
    // Reading the stock font from a DPI-unaware thread context returns the
    // 96-dpi LOGFONT, so the design baseline is recovered exactly. Computed
    // once - the value cannot change without a restart, since it follows the
    // process manifest rather than the current monitor.
    //
    // This is the ONLY font source in this process that is independent of both
    // the system DPI and the monitor. SettingsDpi multiplies it by the scale
    // of the monitor the dialog is on; nothing else may read a default font
    // for the settings UI.
    let private legacyDialogFontCell =
        lazy (
            try
                withUnawareContext <| fun() ->
                    let handle = DpiApi.GetDefaultGuiFontHandle()
                    if handle = IntPtr.Zero then None
                    else
                        // FromHfont does not take ownership of the stock object,
                        // and the Font it returns must not outlive this scope,
                        // so clone it into one this process owns.
                        use borrowed = Font.FromHfont(handle)
                        Some(new Font(borrowed, borrowed.Style))
            with _ -> None)

    /// The font a DPI-unaware process would have used for a dialog, or None
    /// when the stock object could not be read (callers then leave the form on
    /// the WinForms default, exactly as before this existed).
    let legacyDialogFont() = legacyDialogFontCell.Force()

    /// Em size of `font` in 96-dpi pixels.
    ///
    /// Font.SizeInPoints is NOT usable for this: it converts through a screen
    /// DC, which on a Per-Monitor-V2 thread reports the SYSTEM dpi, so a 12-px
    /// font would answer 7.2 pt on a 125% desktop and 9 pt on a 100% one. The
    /// Unit / Size pair below is fixed at construction time and cannot drift.
    let pixelEmSize (font: Font) =
        match font.Unit with
        | GraphicsUnit.Pixel | GraphicsUnit.World -> font.Size
        | GraphicsUnit.Point -> font.Size * 96.0f / 72.0f
        | GraphicsUnit.Document -> font.Size * 96.0f / 300.0f
        | GraphicsUnit.Inch -> font.Size * 96.0f
        | GraphicsUnit.Millimeter -> font.Size * 96.0f / 25.4f
        | _ -> font.Size

// Application icons at an arbitrary physical size.
//
// A window publishes its icon in whatever sizes the app chose to ship, and
// WM_GETICON only exposes two of them: ICON_SMALL (usually 16 px) and
// ICON_BIG (usually 32 px). The tab's icon box is a DEVICE-pixel size that
// follows the monitor scale - 17 px at 100%, 25 px at 150%, 33 px at 200%,
// 41 px at 250% - so neither published handle is the right size on its own,
// and simply stretching one of them re-introduces exactly the softness this
// change is about.
//
// CopyImage with LR_COPYFROMRESOURCE asks Windows to go back to the icon's
// original resource and produce the requested size from the closest image it
// finds there (icons commonly carry 16/24/32/48/256), which is the sharpest
// source available.
module ScaledIcon =

    [<Literal>]
    let private cacheLimit = 128

    // Keyed by (source HICON, requested size). Values own their HICON, so an
    // eviction has to destroy it; Icon.FromHandle does not take ownership.
    //
    // The cache is per THREAD, exactly like the font cache above, and for the
    // same reason turned inside out: eviction here DESTROYS a GDI icon handle.
    // Every tab group renders on its own thread, so a process-wide dictionary
    // meant that group A removing a tab (or merely overflowing the limit)
    // could DestroyIcon a handle group B's thread was in the middle of
    // drawing. IconSprite swallows the resulting exception, so the only
    // symptom would have been an icon vanishing for a single frame - a bug
    // that is almost impossible to track down from a report.
    //
    // Owned by one thread, the cache is filled, read and evicted only between
    // that thread's own draw calls, which are sequential, so an entry can
    // never be destroyed while it is being drawn and no lock is needed.
    //
    // A group thread's dictionary becomes unreachable when the thread exits.
    // The handles it still held would leak, so TabStrip.removeTab invalidates
    // on every tab removal - including the last one, which is what runs just
    // before a group's thread finishes.
    let private cache =
        new Threading.ThreadLocal<Collections.Generic.Dictionary<IntPtr * int, Icon * IntPtr>>(
            fun () -> Collections.Generic.Dictionary<IntPtr * int, Icon * IntPtr>())

    let private clear (c: Collections.Generic.Dictionary<IntPtr * int, Icon * IntPtr>) =
        for kv in c do
            let (_, handle) = kv.Value
            DpiApi.DestroyScaledIcon(handle)
        c.Clear()

    /// Drop every icon cached by the CALLING thread. Called when a tab leaves
    /// a strip: Windows recycles HICON values once the owning window is gone,
    /// and the cache is keyed by handle, so a recycled handle must not hit a
    /// stale entry. Other groups' caches are none of this thread's business -
    /// they are keyed by the handles of their own windows and are invalidated
    /// by their own removeTab.
    let invalidate() =
        try clear cache.Value with _ -> ()

    /// `icon` rendered at `size` x `size` device pixels. Returns the original
    /// icon when it is already that size, when the size is not usable, or when
    /// Windows refuses to re-extract - the caller then draws it stretched,
    /// exactly as before this change.
    let at (icon: Icon) (size: int) : Icon =
        if isNull (box icon) || size <= 0 then icon
        else
            try
                let source = icon.Handle
                if source = IntPtr.Zero || icon.Width = size then icon
                else
                    let c = cache.Value
                    let key = (source, size)
                    match c.TryGetValue(key) with
                    | true, (cached, _) -> cached
                    | _ ->
                        let handle = DpiApi.CopyIconAtSize(source, size)
                        if handle = IntPtr.Zero then icon
                        else
                            let scaled = Icon.FromHandle(handle)
                            if c.Count >= cacheLimit then clear c
                            c.[key] <- (scaled, handle)
                            scaled
            with _ -> icon

// DPI scaling for the settings dialog.
//
// The dialog is not self-drawn. WinForms, comctl32 and Aga.Controls put the
// pixels down, so there is no single place to multiply by, the way there was
// for the tab strips. Until this change the dialog was built on a DPI-UNAWARE
// thread (ManagerViewService): Windows laid it out in 96-dpi units and
// bitmap-stretched the whole window on any scaled monitor, which is the blur
// this module exists to remove. It is now built on the process's ordinary
// Per-Monitor-V2 thread, and the 96-dpi design numbers are multiplied here.
//
// ONE factor decides everything: the scale of the monitor the window opens on,
// taken once (SettingsDpi.forCursor) and held in `currentScale`. Fonts and
// layout are multiplied by the SAME factor, which is what makes the result
// scale-invariant: a caption that fits its label column at 100% fits it at
// every scale, in every one of the sixteen languages, and one that wraps at
// 100% wraps into the same number of lines at every scale.
//
// Three mechanisms carry the multiplication:
//
//  * The FONT. A point-sized font cannot be used in a Per-Monitor-V2 process:
//    GDI+ converts points to pixels with the DPI of a screen DC, which for an
//    aware thread is the SYSTEM dpi - 120 on this desktop - whatever monitor
//    the window is on. Worse, the stock UI font Windows hands an AWARE thread
//    is already enlarged by the system scale before GDI+ sees it (9 pt / 12 px
//    becomes 11.25 pt / 15 px at 125%), which is what made the settings labels
//    25% too large and wrap. Both are removed the same way: read the baseline
//    once from a DPI-UNAWARE thread (Dpi.legacyDialogFont, 12 px), then hand
//    out GraphicsUnit.Pixel fonts of baseline * scale. 100% -> 12 px,
//    150% -> 18 px, 175% -> 21 px, and no system-DPI term anywhere.
//
//  * Control.Scale, WinForms' own scaling walk. It multiplies every control's
//    Bounds, Padding, Margin and Minimum/MaximumSize, and - the reason this
//    legacy dialog can be scaled at all - the ABSOLUTE row and column styles
//    of a TableLayoutPanel, which is what nearly every panel in the settings
//    views is built from (the 250-px label column, the 35-px rows). AutoSize
//    controls are scaled and then snap back to whatever the new font asks for,
//    so labels grow with their text instead of being clipped.
//
//  * The fixups below, for what Control.Scale cannot reach: Aga's TreeViewAdv
//    (its own font, row height, indent, column widths, node-control margins,
//    column header height), ToolStrip image sizes and owner-drawn item heights.
//
// `current()` is the scale in force right now. Owner-drawn code on a paint
// path reads it rather than capturing a factor, so a monitor change while the
// dialog is open is picked up on the next repaint.
module SettingsDpi =
    open Aga.Controls.Tree
    open Aga.Controls.Tree.NodeControls

    let mutable private currentScale = 1.0

    /// The scale the settings windows are laid out for right now. 1.0 when
    /// none has been opened yet, so everything that reads this is inert
    /// outside the dialog.
    let current() = currentScale

    /// Record the scale the settings UI is about to be built for. Called
    /// before the views are constructed so anything that reads `px` while
    /// building sees it.
    let setCurrent (scale: float) = currentScale <- scale

    /// Scale a 96-dpi design length to the current settings scale.
    let px (value: int) = Dpi.px currentScale value

    /// Scale a 96-dpi design length that GDI+ draws at sub-pixel precision
    /// (pen widths).
    let pxf (value: float32) = Dpi.pxf currentScale value

    /// Scale of the monitor the mouse pointer is on. This is the monitor the
    /// dialog opens on: Form.CenterToScreen falls back to
    /// Screen.FromPoint(Control.MousePosition) for an ownerless form, so both
    /// the tray icon and the tab context menu already put the dialog where the
    /// click happened, and that is preserved.
    let forCursor() =
        try Dpi.scaleForPoint (Control.MousePosition.Pt) with _ -> 1.0

    // One Font per (family, pixel size, style, charset), kept for the process
    // lifetime.
    //
    // Deliberately NOT Dpi.scaledFont: that cache is shared with the tab
    // strips and DISPOSES every entry when it overflows, which is safe for
    // code that re-requests a font on each repaint but not for a Font that a
    // live Control still holds. The key space here is (the two font families
    // the dialog uses) x (the set of monitor scales on the desktop), so this
    // dictionary cannot grow.
    let private fonts = Collections.Generic.Dictionary<string * float32 * FontStyle * byte, Font>()
    let private fontsLock = obj()

    let private pixelFont (family: string) (pixelSize: float32) (style: FontStyle) (charSet: byte) =
        lock fontsLock <| fun() ->
            let key = (family, pixelSize, style, charSet)
            match fonts.TryGetValue(key) with
            | true, cached -> cached
            | _ ->
                let created = new Font(family, pixelSize, style, GraphicsUnit.Pixel, charSet, false)
                fonts.[key] <- created
                created

    /// A pixel-sized copy of `font` enlarged by `scale`, keeping the charset
    /// (which matters for the CJK stock fonts) and the size the original
    /// occupies on a 96-dpi surface.
    let scaledCopy (font: Font) (scale: float) =
        try pixelFont font.FontFamily.Name (Dpi.pxf scale (Dpi.pixelEmSize font)) font.Style font.GdiCharSet
        with _ -> font

    /// The settings dialog font at `scale`, in pixel units so that neither the
    /// system DPI nor the destination surface can change its size. None when
    /// the 96-dpi stock font could not be read, in which case callers leave
    /// the window on the WinForms default.
    let font (scale: float) =
        match Dpi.legacyDialogFont() with
        | None -> None
        // Hand back the baseline itself at 100% so an unscaled monitor gets
        // the exact Font object the pre-DPI dialog was using.
        | Some(baseFont) when scale = 1.0 -> Some(baseFont)
        | Some(baseFont) -> Some(scaledCopy baseFont scale)

    /// Side of the tree check-box glyph at the current scale. Both check-box
    /// variants (DarkNodeCheckBox and ScaledNodeCheckBox) draw the glyph
    /// themselves, so both follow this.
    let checkBoxSize() = px NodeCheckBox.ImageSize

    /// Side of the glyph a WinForms CheckBox or RadioButton draws, at the
    /// current scale.
    ///
    /// WinForms draws it 13 px wide on every monitor - the constant is baked
    /// into ButtonBase's layout and .NET Framework 4.7's own high-DPI scaling,
    /// which would move it, is deliberately switched off here (see
    /// SettingsDialogWindow). Measured: a caption-less CheckBox is 18 x 17 and
    /// its caption starts 20 px from the left edge at 100%, and both numbers
    /// are unchanged at 125 / 150 / 175% while the font goes 12 -> 21 px. So
    /// the glyph shrinks relative to everything around it as the scale rises,
    /// and the only way to grow it is to draw over it - which is what the
    /// dark-mode overpaint in DarkMode does.
    let winFormsGlyphSize() = px 13

    // TreeViewAdv fixes its column header height in the constructor (20 px
    // with visual styles on - treeviewadv/Aga.Controls/Tree/TreeViewAdv.cs:216)
    // and exposes neither a setter nor a protected hook: the field is
    // `private readonly int _columnHeaderHeight` (:28) and the property that
    // reads it is `internal` (TreeViewAdv.Properties.cs:55). At 150% the
    // scaled font no longer fits in 20 px, so the header text would be clipped
    // top and bottom - one of the two failures the old "do not make this
    // aware" comment warned about.
    //
    // Aga.Controls is referenced as a prebuilt assembly (WtProgram.fsproj),
    // so reflection is the only way in without taking the treeviewadv/ sources
    // into the solution. The field name is verifiable in-tree at the two
    // source lines above. If a future rebuild renames it this is null and the
    // header simply stays 20 px: a cosmetic regression at scaled DPI, not a
    // crash, and 100% is unaffected either way.
    let private headerHeightField =
        try typeof<TreeViewAdv>.GetField("_columnHeaderHeight", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)
        with _ -> null

    /// Height a TreeViewAdv column header needs for the font it is currently
    /// using. Used both for Aga's own layout (via the field above) and by the
    /// dark-mode header overpaint, so the two always agree. At scale 1.0 this
    /// is the `max 24 (Font.Height + 8)` the dark overlay has always used.
    let treeHeaderHeight (tva: TreeViewAdv) =
        max (px 24) (tva.Font.Height + px 8)

    // The 96-dpi design values a control was built with, captured the first
    // time it is scaled. Everything derived from them is then computed
    // ABSOLUTELY from the target scale rather than by multiplying the current
    // value, so walking 125% -> 150% -> 175% -> 100% -> 125% lands on exactly
    // the numbers a fresh 125% window would have. (Column widths are the one
    // exception - see scaleTree.)
    type private TreeDesign(tva: TreeViewAdv) =
        member val font = tva.Font
        member val rowHeight = tva.RowHeight
        member val indent = tva.Indent
        member val leftMargins = [| for c in tva.NodeControls -> c.LeftMargin |]

    type private StripDesign(strip: ToolStrip) =
        member val imageScalingSize = strip.ImageScalingSize

    type private ComboDesign(combo: ComboBox) =
        member val itemHeight = combo.ItemHeight

    let private treeDesigns = Runtime.CompilerServices.ConditionalWeakTable<TreeViewAdv, TreeDesign>()
    let private stripDesigns = Runtime.CompilerServices.ConditionalWeakTable<ToolStrip, StripDesign>()
    let private comboDesigns = Runtime.CompilerServices.ConditionalWeakTable<ComboBox, ComboDesign>()

    // The height of a settings row has TWO independent ways of coming out too
    // large on a scaled monitor, and because the row takes the MAXIMUM of what
    // its label asks for and what its editor asks for, fixing either one alone
    // changes nothing. Both are fixed here: the ratchet immediately below, and
    // the doubled Margin / MinimumSize further down. Measured on a replica of
    // the Behavior-tab rows (row pitch at 175%, design 35 px -> 61 px):
    //
    //   nothing fixed, ratchet only                 82
    //   nothing fixed, label scaled twice          106   <- what the user sees
    //   ratchet fixed only                         106
    //   Margin / MinimumSize fixed only             82
    //   both fixed                                  62
    //
    // 106 is the number the user's 175% dialog actually showed, so any fix
    // that does not bring THAT column down is not a fix at all.
    //
    // Cause 1, the ratchet. A control that FILLS a TableLayoutPanel cell (the
    // editor column of the settings forms - check boxes, combos, hot-key
    // editors) has no size of its own: Dock = Fill stretches it to whatever
    // the cell is. But the AutoSize row that cell belongs to measures a
    // non-AutoSize control by its SPECIFIED size (TableLayout.GetElementSize
    // falls back to CommonProperties.GetSpecifiedBounds), and Control.Scale
    // ends by writing "current bounds x factor" back as the specified size
    // (ScaleControl's closing SetBoundsCore(..., BoundsSpecified.All)).
    // Together they are a ratchet: the row stretches the control, the next
    // scale freezes the stretched height in as a demand, and the row can only
    // grow.
    //
    // So the design HEIGHT of every such control is captured the first time it
    // comes through applyScale, and after every Control.Scale its specified
    // height is re-asserted ABSOLUTELY as design x scale. The rows are then
    // driven by their design numbers (label margins + minimum height) at every
    // scale and after any number of crossings. The re-assert goes through
    // SetBounds, whose BoundsSpecified.All also keeps the control's
    // RequiredScaling a full set, so the next Control.Scale multiplies its
    // Margin / Padding / MinimumSize exactly once (a PARTIAL set makes
    // Control.ScaleControl run its two-pass split and scale those properties
    // twice - see applyControlLayout). Dock re-stretches the visible bounds on
    // the next layout pass; only the recorded demand changes.
    //
    // The WIDTH is deliberately left alone. Only the height was ever wrong,
    // and the "design width" of a control in a Percent column is nothing but
    // the width the window happened to have when it was captured - re-asserting
    // design width x scale would hold a demand that big after the user has
    // narrowed the dialog, which in an AutoSize + AutoScroll panel is how a
    // horizontal scrollbar appears.
    type private CellDesign(height: int) =
        member val height = height

    let private cellDesigns = Runtime.CompilerServices.ConditionalWeakTable<Control, CellDesign>()

    let private isTableCellFiller (control: Control) =
        control.Dock = DockStyle.Fill
        && not control.AutoSize
        && (match control.Parent with
            | :? TableLayoutPanel -> true
            | _ -> false)

    let rec private captureCellDesigns (control: Control) (fromScale: float) =
        if isTableCellFiller control then
            cellDesigns.GetValue(control, fun c ->
                CellDesign(max 1 (int (Math.Round(float c.Height / fromScale, MidpointRounding.AwayFromZero))))) |> ignore
        for child in control.Controls do
            captureCellDesigns child fromScale

    let rec private reassertCellDesigns (control: Control) (toScale: float) =
        if isTableCellFiller control then
            match cellDesigns.TryGetValue(control) with
            | true, design ->
                try
                    control.SetBounds(control.Left, control.Top, control.Width,
                                      max 1 (Dpi.px toScale design.height))
                with _ -> ()
            | _ -> ()
        for child in control.Controls do
            reassertCellDesigns child toScale

    // Cause 2, the doubled layout constraints.
    //
    // Control.ScaleControl(includedFactor, excludedFactor, requesting) splits
    // its work by the control's RequiredScaling: the included factor is
    // applied to the bits that are IN the set and the excluded factor to the
    // bits that are NOT. Control.Scale(factor) passes the same non-empty
    // factor as both, so a control whose RequiredScaling is a PROPER SUBSET of
    // BoundsSpecified.All gets ScaleControl(factor, specified) called TWICE.
    // That overload applies `specified` to the Bounds only - Margin, Padding,
    // MinimumSize and MaximumSize are multiplied on every call, unconditionally.
    // Bounds therefore scale once while the constraints scale twice, which is
    // exactly the shape the user measured at 175%: window width 1400 (once),
    // glyph 24 px (once), radio row 38 px (once, AutoSize so it is measured
    // from its preferred size), but the check-box rows at 106 px:
    //
    //   MinimumSize 22 -> 38 -> 66      Margin.Top 8 -> 14 -> 24
    //                                   Margin.Bottom 5 -> 9 -> 16
    //   row = 66 + 24 + 16 = 106, and 106 is font-independent - it is just the
    //   design numbers multiplied twice.
    //
    // Rather than hunt for what leaves RequiredScaling partial (it survives
    // any Size / Width / Height assignment made after a previous scale reset
    // it), the three properties are recorded once in their 96-dpi design form
    // and recomputed ABSOLUTELY from the target scale after every scale pass.
    // Then it does not matter how many times Control.Scale multiplied them, or
    // in which order the monitors were crossed: 175 -> 100 -> 175 lands on the
    // same numbers a window freshly opened at 175% has.
    type private ControlLayoutDesign(control: Control) =
        member val margin = control.Margin
        member val minimumSize = control.MinimumSize
        member val maximumSize = control.MaximumSize

    let private controlLayoutDesigns = Runtime.CompilerServices.ConditionalWeakTable<Control, ControlLayoutDesign>()

    let rec private captureControlLayout (control: Control) =
        controlLayoutDesigns.GetValue(control, fun c -> ControlLayoutDesign(c)) |> ignore
        for child in control.Controls do
            captureControlLayout child

    /// Scale one dimension of a MinimumSize / MaximumSize. Zero is not a
    /// length in those two properties, it is "no constraint" - MaximumSize
    /// (0, 0) is what every control in the dialog carries and it means
    /// unlimited - so zero has to come out of the multiplication as zero.
    let private scaleConstraint (scale: float) (value: int) =
        if value = 0 then 0 else max 1 (Dpi.px scale value)

    // Every control is visited, so each property is compared before it is
    // assigned: all three setters start a layout transaction on the parent,
    // and at 100% (where design = current for everything) this walk must not
    // cost a single one of them.
    let rec private applyControlLayout (control: Control) (scale: float) =
        (match controlLayoutDesigns.TryGetValue(control) with
         | true, design ->
            try
                let d = design.margin
                let margin =
                    Padding(Dpi.px scale d.Left, Dpi.px scale d.Top,
                            Dpi.px scale d.Right, Dpi.px scale d.Bottom)
                if control.Margin <> margin then control.Margin <- margin
                let minimumSize =
                    Size(scaleConstraint scale design.minimumSize.Width,
                         scaleConstraint scale design.minimumSize.Height)
                if control.MinimumSize <> minimumSize then control.MinimumSize <- minimumSize
                let maximumSize =
                    Size(scaleConstraint scale design.maximumSize.Width,
                         scaleConstraint scale design.maximumSize.Height)
                if control.MaximumSize <> maximumSize then control.MaximumSize <- maximumSize
            with _ -> ()
         | _ -> ())
        for child in control.Controls do
            applyControlLayout child scale

    // The dark-mode glyph reservation. DarkMode reserves (scaled glyph - 13)
    // px of left Padding on every CheckBox / RadioButton so its enlarged
    // overpaint does not run over the caption. The reservation is registered
    // here because it is 13 * (scale - 1) px - NOT proportional to the scale -
    // so letting Control.Scale multiply the old value leaves the wrong number
    // after a monitor change (10 px put on at 175% became 6 px on a 100%
    // monitor instead of 0). fixups below recomputes it absolutely instead.
    let private glyphPadded = Runtime.CompilerServices.ConditionalWeakTable<Control, obj>()

    let private glyphPaddingAt (scale: float) =
        Padding(max 0 (Dpi.px scale 13 - 13), 0, 0, 0)

    let private roundTo (factor: float) (value: int) =
        int (Math.Round(float value * factor, MidpointRounding.AwayFromZero))

    let private scaleTree (tva: TreeViewAdv) (toScale: float) (factor: float) =
        try
            let design = treeDesigns.GetValue(tva, fun t -> TreeDesign(t))
            // TreeViewAdv assigns its own Font in its constructor
            // (TreeViewAdv.cs:244, a static Tahoma 8.25 POINT font), so unlike
            // every other control in the dialog it does not inherit the form's
            // font and has to be given one. Point-sized, it would follow the
            // system DPI on every monitor; converted to the pixel size it
            // occupies at 96 dpi and multiplied by the monitor scale, it does
            // not. This runs at 100% too, where it is the same metrics the
            // font has always produced on a 96-dpi system.
            tva.Font <- scaledCopy design.font toScale
            tva.RowHeight <- max 1 (Dpi.px toScale design.rowHeight)
            tva.Indent <- max 1 (Dpi.px toScale design.indent)
            // Column widths are the one metric the user can change (the header
            // dividers are draggable), so they are carried across by the
            // relative factor instead of being reset to the design value.
            if factor <> 1.0 then
                for column in tva.Columns do
                    column.Width <- max 1 (roundTo factor column.Width)
            let glyph = Dpi.px toScale NodeCheckBox.ImageSize
            let margins = design.leftMargins
            let mutable i = 0
            for control in tva.NodeControls do
                match control with
                | :? NodeCheckBox as checkBox when checkBox.ParentColumn <> null ->
                    // These columns centre their box by hand (ProgramView), so
                    // re-derive the margin from the column as it is now rather
                    // than scaling the old margin.
                    checkBox.LeftMargin <- max 0 ((checkBox.ParentColumn.Width - glyph) / 2)
                | _ ->
                    if i < margins.Length then
                        control.LeftMargin <- max 0 (Dpi.px toScale margins.[i])
                i <- i + 1
            // Left at Aga's stock 20 px on an unscaled monitor so nothing
            // moves at 100%; the dark overpaint's own historical 24 px is
            // unchanged there too.
            if toScale <> 1.0 && headerHeightField <> null then
                headerHeightField.SetValue(tva, box (treeHeaderHeight tva))
            tva.FullUpdate()
        with _ -> ()

    let private scaleToolStrip (strip: ToolStrip) (toScale: float) =
        // ToolStripItem.Image re-fits itself to the strip's ImageScalingSize,
        // so this is the whole of toolbar icon scaling. The 48-px sources
        // (delete.png, edit.png) come out sharper than the 16-px ones.
        try
            let design = stripDesigns.GetValue(strip, fun s -> StripDesign(s))
            let size = design.imageScalingSize
            strip.ImageScalingSize <-
                Size(max 1 (Dpi.px toScale size.Width), max 1 (Dpi.px toScale size.Height))
        with _ -> ()
        // A ToolStrip does NOT inherit the form's font: its default comes from
        // ToolStripManager (SystemFonts.MenuFont), which follows the SYSTEM
        // dpi. So the assignment applyScale makes to form.Font reaches every
        // other control but leaves toolbar captions at the primary monitor's
        // size - tiny next to 175% text, invisible on a 100% monitor where the
        // two coincide (the Program / Workspace tab toolbars). Assign the same
        // scaled font the form got. At 100% this hands back the baseline font,
        // so an unscaled monitor keeps the exact pre-DPI toolbar look.
        match font toScale with
        | Some(f) -> (try strip.Font <- f with _ -> ())
        | None -> ()

    let private scaleComboBox (combo: ComboBox) (toScale: float) =
        // Owner-draw item heights are not part of Bounds, so Control.Scale
        // never sees them. The MeasureItem handler that goes with this one
        // (AppearanceView) computes the same number from `px`.
        try
            if combo.DrawMode <> DrawMode.Normal then
                let design = comboDesigns.GetValue(combo, fun c -> ComboDesign(c))
                combo.ItemHeight <- max 1 (Dpi.px toScale design.itemHeight)
        with _ -> ()

    // Control.Scale walks the tree writing an explicit Bounds onto every
    // control it visits, so a control it reaches before the layout has run
    // gets a MEANINGLESS size frozen in as if it had been asked for.
    //
    // That is not a hypothetical. A window that has been given its size but
    // never laid out still has its panels at their defaults, and a TabControl
    // makes it worse: it only ever lays out the page it is SHOWING, so the
    // three pages behind the first one are at TabPage's 200 x 100 default when
    // the dialog is scaled. In the Appearance panel, whose label column is a
    // fixed 250 px, that left the Percent column with a width of -164 and
    // every control in it 1 px wide. Anything that fills, stretches or
    // auto-sizes came back on the next layout pass; the one control that does
    // none of those - the dark-mode check box - stayed 1 px wide and painted
    // as a single grey line.
    //
    // Measured, at 175% (a replica of this control tree built from the same
    // source, laid out and read back without running WindowTabs):
    //
    //   nothing                        column widths 250,-164,80   checkbox 2 x 42
    //   form.PerformLayout() only      column widths 250,-164,80   checkbox 2 x 42
    //   assign the clamped Bounds
    //     before Control.Scale         column widths 250,-164,80   checkbox 2 x 42
    //   this function                  column widths 250, 418,80   checkbox 182 x 42
    //
    // The middle two are worth spelling out: neither the obvious PerformLayout
    // nor re-ordering the Bounds assignment reaches the pages that are not on
    // screen. Handing every page the display rectangle explicitly does, and it
    // is quieter than walking SelectedIndex over all of them, which would fire
    // selection events at views that are still being constructed.
    let rec private settleTabPages (control: Control) =
        match control with
        | :? TabControl as tabs ->
            // Read AFTER the form-level pass below has sized the TabControl -
            // DisplayRectangle is derived from its Bounds, which are still the
            // 200 x 100 default until something lays the form out.
            let display = tabs.DisplayRectangle
            for page in tabs.TabPages do
                if page.Bounds <> display then page.Bounds <- display
                page.PerformLayout()
        | _ -> ()
        for child in control.Controls do
            settleTabPages child

    let private settleLayout (form: Form) =
        form.PerformLayout()
        settleTabPages (form :> Control)

    let rec private fixups (control: Control) (toScale: float) (factor: float) =
        match control with
        | :? TreeViewAdv as tva -> scaleTree tva toScale factor
        | :? ToolStrip as strip -> scaleToolStrip strip toScale
        | :? ComboBox as combo -> scaleComboBox combo toScale
        | _ -> ()
        // Absolute, not the Padding Control.Scale just multiplied - see
        // glyphPadded above.
        (match glyphPadded.TryGetValue(control) with
         | true, _ -> (try control.Padding <- glyphPaddingAt toScale with _ -> ())
         | _ -> ())
        for child in control.Controls do
            fixups child toScale factor

    /// Re-lay a settings window out from `fromScale` to `toScale`. Windows
    /// built in 96-dpi design units pass fromScale = 1.0; the running dialog
    /// passes its previous scale when it moves to another monitor.
    ///
    /// The font is assigned even when nothing else changes: the WinForms
    /// default font follows the SYSTEM dpi on an aware thread, so leaving it
    /// alone would enlarge the dialog by 25% on a 100% monitor of a desktop
    /// whose primary monitor is at 125%.
    let applyScale (form: Form) (fromScale: float) (toScale: float) =
        currentScale <- toScale
        // Before anything else: give every control the size the 96-dpi design
        // says it has, so Control.Scale multiplies real numbers rather than
        // freezing in the ones a never-laid-out panel happens to hold.
        try settleLayout form with _ -> ()
        // Capture the design values while the controls still hold them - the
        // first pass for any window passes fromScale = 1.0 (every call site
        // does; only the running dialog's monitor change passes anything else,
        // and by then the window has been through here once) and the tree was
        // just laid out in design units. Both captures are idempotent, which
        // is why they run on every pass: a control added to the window after
        // it was built gets a design record on the next scale instead of none
        // at all.
        try captureCellDesigns (form :> Control) fromScale with _ -> ()
        try captureControlLayout (form :> Control) with _ -> ()
        match font toScale with
        | Some(f) -> (try form.Font <- f with _ -> ())
        | None -> ()
        let factor = toScale / fromScale
        if factor <> 1.0 then
            try
                let f = float32 factor
                form.Scale(SizeF(f, f))
            with _ -> ()
            // Control.Scale just recorded every filler's stretched bounds as
            // its specified size; put the design number back (see cellDesigns)
            // so the AutoSize rows stay 35 x scale instead of ratcheting.
            try reassertCellDesigns (form :> Control) toScale with _ -> ()
        // AFTER reassertCellDesigns, so that its SetBounds - which is what
        // leaves RequiredScaling a full set - is not undone, and at factor 1.0
        // as well, where every design value already equals the current one and
        // the walk assigns nothing.
        try applyControlLayout (form :> Control) toScale with _ -> ()
        // Runs at factor 1.0 as well, because the metrics it fixes are not
        // inherited from the form and the TreeViewAdv font is one of them.
        fixups (form :> Control) toScale factor

    /// Reserve room on a CheckBox / RadioButton for the dark-mode glyph
    /// overpaint, and register the control so every later applyScale
    /// recomputes the reservation absolutely for the monitor it is then on.
    let applyGlyphPadding (control: Control) =
        glyphPadded.GetValue(control, fun _ -> null) |> ignore
        try control.Padding <- glyphPaddingAt currentScale with _ -> ()

    /// Lay a child dialog of the settings window out at the scale the parent
    /// is using. Child dialogs open CenterParent, so that is by construction
    /// the scale of the monitor they appear on.
    let applyToChildDialog (form: Form) = applyScale form 1.0 currentScale

    /// Lay a stand-alone dialog out for the monitor the pointer is on (the
    /// "language changed" confirmation, which is opened from the tray menu
    /// after the settings dialog has already closed).
    let applyAtCursor (form: Form) = applyScale form 1.0 (forCursor())

    /// A copy of `image` at the current scale, for the small PNGs the tree
    /// node icons are built from. Returns the original at 100%.
    let scaledImage (image: Image) : Image =
        if currentScale = 1.0 || isNull (box image) then image
        else
            try
                let w = max 1 (Dpi.px currentScale image.Width)
                let h = max 1 (Dpi.px currentScale image.Height)
                let bitmap = new Bitmap(w, h)
                // Pinned to 96 dpi for the same reason Img is - a new Bitmap
                // inherits the process's awareness and would otherwise carry
                // the system DPI into every later DrawImage.
                bitmap.SetResolution(96.0f, 96.0f)
                use g = Graphics.FromImage(bitmap)
                g.InterpolationMode <- Drawing2D.InterpolationMode.HighQualityBicubic
                g.DrawImage(image, Rectangle(0, 0, w, h))
                bitmap :> Image
            with _ -> image

    /// `desired` clamped to the FULL rectangle of the monitor it sits on, and
    /// nudged back inside it if the clamp left it hanging over an edge.
    ///
    /// The full monitor, not the working area: the user explicitly accepts the
    /// dialog overlapping the taskbar, and the working-area clamp is what cut
    /// a 1400 x 1050 dialog to 1400 x 996 on a 1080-px 175% monitor and put a
    /// scrollbar on every tab. 1050 < 1080, so against the monitor itself no
    /// clamp fires there at all; only a monitor the dialog genuinely cannot
    /// fit on (200%+ of the same panel) still shrinks it - display-only, the
    /// 96-dpi design size is kept elsewhere (see setWindowDesign).
    let fitToScreen (desired: Rect) =
        try
            let centre = Pt(desired.x + desired.width / 2, desired.y + desired.height / 2)
            match Mon.nearestFromPoint centre with
            | Some(mon) ->
                let screen = mon.displayRect
                let w = min desired.width screen.width
                let h = min desired.height screen.height
                let x = min (max desired.x screen.x) (screen.right - w)
                let y = min (max desired.y screen.y) (screen.bottom - h)
                Rect(Pt(x, y), Sz(w, h))
            | None -> desired
        with _ -> desired

    /// A window of `size` centred in the working area of the monitor under
    /// `probe` - what FormStartPosition.CenterScreen would have produced - but
    /// clamped to the FULL monitor rather than the working area. On a monitor
    /// where the dialog fits the working area nothing moves; where it only
    /// fits the monitor, it is pushed up to the monitor edge and its bottom
    /// lies over the taskbar, which the user prefers to a shrunken window.
    let centeredAt (probe: Pt) (size: Sz) =
        try
            match Mon.nearestFromPoint probe with
            | Some(mon) ->
                let work = mon.workRect
                let screen = mon.displayRect
                let w = min size.width screen.width
                let h = min size.height screen.height
                let x = min (max (work.x + (work.width - w) / 2) screen.x) (screen.right - w)
                let y = min (max (work.y + (work.height - h) / 2) screen.y) (screen.bottom - h)
                Rect(Pt(x, y), Sz(w, h))
            | None -> Rect(Pt(), size)
        with _ -> Rect(Pt(), size)

    // The 96-dpi size a settings window is laid out for.
    //
    // Everything else in the dialog gets its device size by multiplying a
    // design number by the scale. The window itself did not: it was CLAMPED
    // (above; to the working area when this was written, to the full monitor
    // since R11), and the clamped size was then the only record of how big the
    // window was. A 1400 x 1050 window clamped on a 996-px-tall working area
    // shows as 1400 x 996, and 996 is not 600 logical pixels, it
    // is 569 - so the rectangle Windows suggests on the next WM_DPICHANGED
    // (this window's size multiplied by the DPI ratio) is 569 logical pixels
    // too. The dialog opened at 175% came out 1000 x 711 on the 125% monitor
    // instead of 1000 x 750, 1200 x 853 instead of 1200 x 900, and never got
    // its height back, because nothing remembered that 600 was ever intended.
    //
    // Keeping the design size here separates the two ideas. The clamp stays
    // what it always was - a limit on what THIS monitor can show, with the
    // content scrolling inside it - and stops being a resize. Every later scale
    // is computed from the design size, so a window that did not fit where it
    // was opened is its proper size on every monitor where it does fit, and
    // crossing back and forth cannot accumulate rounding: 800 x 600 is always
    // the multiplicand.
    type private WindowDesign(size: Sz) =
        /// What the window is laid out for, in 96-dpi units.
        member val logicalSize = size with get, set
        /// The device size this module last put on the window. A dimension that
        /// still holds this value is one nobody has touched since, which is how
        /// a user resize of the WIDTH is kept from also adopting a height the
        /// clamp had reduced.
        member val appliedSize = Sz() with get, set

    let private windowDesigns = Runtime.CompilerServices.ConditionalWeakTable<Form, WindowDesign>()

    /// Record the 96-dpi size `form` is laid out for. Called once, while the
    /// window is being built, and again whenever the user resizes it by hand.
    let setWindowDesign (form: Form) (size: Sz) =
        try
            if size.width > 0 && size.height > 0 then
                (windowDesigns.GetValue(form, fun _ -> WindowDesign(size))).logicalSize <- size
        with _ -> ()

    /// The device size `form` should have at `scale`, from its 96-dpi design
    /// size. A window that was never registered answers with the size it has,
    /// which is what the caller would have used anyway.
    let windowSizeAt (form: Form) (scale: float) =
        try
            match windowDesigns.TryGetValue(form) with
            | true, design ->
                Sz(max 1 (Dpi.px scale design.logicalSize.width),
                   max 1 (Dpi.px scale design.logicalSize.height))
            | _ -> Sz(form.Width, form.Height)
        with _ -> Sz(form.Width, form.Height)

    /// Put `form` at `desired`, clamped into the full monitor it lands on.
    /// The clamp is display-only: it does not touch the design size.
    let placeWindow (form: Form) (desired: Rect) =
        try
            form.Bounds <- (fitToScreen desired).Rectangle
            match windowDesigns.TryGetValue(form) with
            | true, design -> design.appliedSize <- Sz(form.Width, form.Height)
            | _ -> ()
        with _ -> ()

    /// Adopt the size `form` has now as its 96-dpi design size, for a resize
    /// the USER performed on the window border, so that it survives a move to
    /// another monitor the way the original design size does.
    ///
    /// Per dimension, and only where the user actually changed something. A
    /// window opened at 175% on a monitor too short for it is 996 px tall
    /// against a design of 1050; dragging its right-hand edge, or starting a
    /// drag and abandoning it with Escape, must not turn that 996 into the
    /// design and bring the whole defect back through the side door.
    let adoptWindowSize (form: Form) (scale: float) =
        try
            if scale > 0.0 then
                match windowDesigns.TryGetValue(form) with
                | true, design ->
                    let logical (applied: int) (current: int) (existing: int) =
                        if current = applied then existing
                        else max 1 (int (Math.Round(float current / scale, MidpointRounding.AwayFromZero)))
                    let applied = design.appliedSize
                    let current = Sz(form.Width, form.Height)
                    design.logicalSize <-
                        Sz(logical applied.width current.width design.logicalSize.width,
                           logical applied.height current.height design.logicalSize.height)
                    design.appliedSize <- current
                | _ -> ()
        with _ -> ()
