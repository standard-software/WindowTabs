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
//    filled them. WindowTabs is Per-Monitor-V2 aware except for the settings
//    dialog, which is deliberately created on a DPI-unaware thread (see
//    Dpi.withUnawareContext); if that dialog fills the cache, every rectangle
//    in it is virtualized instead of device pixels.
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
                for kv in cache do
                    try kv.Value.Dispose() with _ -> ()
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
    /// the previous context afterwards. Windows created inside keep the
    /// unaware context for their whole lifetime, so the legacy WinForms
    /// settings UI keeps laying itself out in 96-dpi units and is scaled by
    /// the OS exactly as it was before this change.
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
    // sees 9pt. The dialog is deliberately kept DPI-unaware and its layout is
    // hard-coded in 96-dpi pixels (a 250-px label column, 35-px rows), so the
    // 25% larger text overflowed captions that used to fit: they wrapped onto
    // a second line, which the fixed row height then cut off.
    //
    // Reading the stock font from a DPI-unaware thread context returns the
    // 96-dpi LOGFONT, so the dialog gets its original font back. Computed once
    // - the value cannot change without a restart, since it follows the
    // process manifest rather than the current monitor.
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

    /// Give `control` (normally a Form, whose children inherit it) the font a
    /// DPI-unaware process would have used. No-op if it could not be read.
    let applyLegacyDialogFont (control: Control) =
        match legacyDialogFontCell.Force() with
        | Some(font) -> try control.Font <- font with _ -> ()
        | None -> ()

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
