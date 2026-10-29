namespace Bemo
open System
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.IO
open System.Runtime.InteropServices
open System.Threading

// Frosted glass under translucent tabs.
//
// A translucent tab (an inactive group with "fade out tabs on inactive
// windows", or a tab whose window is on another virtual desktop) used to be
// the tab drawn at half opacity, so whatever was behind it - desktop icons,
// their labels - showed through sharp and made the tab's own text hard to
// read. Here the half that showed the screen shows the desktop wallpaper,
// blurred, instead: the tab looks like frosted glass over the desktop.
//
// Why the wallpaper and not the screen, and why drawn here rather than by
// Windows:
//  * DWM's own materials (Desktop Acrylic, Mica) fall back to a solid colour
//    when their window is not the active one, and a tab strip is never
//    activated. The wallpaper is also what the user asked for: icons ignored,
//    the desktop background blurred.
//  * The strip is a per-pixel-alpha layered window, so drawing the glass
//    into its pixels keeps it to the exact shape of each tab, antialiased
//    edges included, and leaves the gaps between tabs as they were.
//
// Whenever the glass cannot be drawn - transparency effects turned off, high
// contrast, no wallpaper information, a picture GDI+ cannot read, a part of
// the strip on no monitor - the strip is shown exactly as before.

/// The pure parts of the glass: where the wallpaper lies on a monitor, the
/// blur, and the per-pixel mix. Pixels are 32bpp BGRA, not premultiplied.
module TabGlassMath =

    /// DESKTOP_WALLPAPER_POSITION values.
    [<Literal>]
    let Center = 0
    [<Literal>]
    let Tile = 1
    [<Literal>]
    let Stretch = 2
    [<Literal>]
    let Fit = 3
    [<Literal>]
    let Fill = 4
    [<Literal>]
    let Span = 5

    /// Where a wallpaper picture of imageW x imageH lands, as (x, y, w, h)
    /// relative to the monitor's top-left. `frame` is the area the picture is
    /// laid out in, relative to the same corner: the monitor itself, or for
    /// Span the bounding box of every monitor. Tile gives the first tile; the
    /// caller repeats it.
    let placement (position: int) (fx: float, fy: float, fw: float, fh: float) (imageW: int, imageH: int) =
        let iw, ih = float (max 1 imageW), float (max 1 imageH)
        let scaled s =
            let w, h = iw * s, ih * s
            fx + (fw - w) / 2.0, fy + (fh - h) / 2.0, w, h
        match position with
        | Center -> scaled 1.0
        | Tile -> fx, fy, iw, ih
        | Stretch -> fx, fy, fw, fh
        | Fit -> scaled (min (fw / iw) (fh / ih))
        | _ -> scaled (max (fw / iw) (fh / ih))

    /// The size of the reduced copy of a monitor that the blur works on: an
    /// eighth of each side. Reducing first is itself most of the blur and
    /// keeps the cached copy small (a 3840x2160 monitor is 480x270).
    let reducedSize (width: int, height: int) = max 1 ((width + 7) / 8), max 1 ((height + 7) / 8)

    /// Reject corrupt counts and oversized sources before enumeration or rendering.
    let validMonitorCount count = count > 0u && count <= 64u
    let validFileSize length = length > 0L && length <= 64L * 1024L * 1024L
    let validImageSize width height =
        width > 0 && height > 0 && int64 width * int64 height <= 32000000L

    let reducedPlacement (width, height) (x, y, w, h) =
        let sw, sh = reducedSize (width, height)
        let kx, ky = float sw / float width, float sh / float height
        x * kx, y * ky, w * kx, h * ky

    /// One box blur pass along rows (horizontal) or columns, edge pixels
    /// repeated, into `dst`.
    let private boxPass (src: byte[]) (dst: byte[]) width height radius horizontal =
        let lines, length = if horizontal then height, width else width, height
        let window = 2 * radius + 1
        let index line i = if horizontal then (line * width + i) * 4 else (i * width + line) * 4
        for line in 0 .. lines - 1 do
            for c in 0 .. 3 do
                let at i = int src.[index line (max 0 (min (length - 1) i)) + c]
                let mutable sum = 0
                for i in -radius .. radius do sum <- sum + at i
                for i in 0 .. length - 1 do
                    dst.[index line i + c] <- byte ((sum + window / 2) / window)
                    sum <- sum + at (i + radius + 1) - at (i - radius)

    /// Three box passes each way, close to a Gaussian blur. In place.
    let blur (pixels: byte[]) width height radius =
        if radius > 0 && width > 0 && height > 0 && pixels.Length >= width * height * 4 then
            let other : byte[] = Array.zeroCreate pixels.Length
            for _ in 1 .. 3 do
                boxPass pixels other width height radius true
                boxPass other pixels width height radius false

    /// One row of the strip with the glass mixed in, in place.
    ///
    /// `row` is the strip's pixels, `backdrop` the blurred wallpaper under
    /// them (alpha 0 where there is none), `dimmed` marks the pixels inside a
    /// tab drawn faded (another virtual desktop), whose own alpha was already
    /// multiplied by `dimmedAlpha`. `windowAlpha` is the opacity the strip was
    /// to be shown at; the result is meant for opacity 255.
    ///
    /// A translucent tab pixel becomes its colour over the blurred wallpaper,
    /// mixed in the proportion it used to be mixed with the screen, at the
    /// coverage it had before fading (so antialiased edges stay soft). Every
    /// other pixel keeps its colour and gets the alpha it effectively had:
    /// alpha * windowAlpha / 255, truncated as the window opacity truncates,
    /// so nothing that let clicks through starts catching them.
    let composeRow (row: byte[]) (backdrop: byte[]) (dimmed: bool[]) (width: int) (windowAlpha: int) (dimmedAlpha: int) =
        let s = max 0 (min 255 windowAlpha)
        let d = max 1 (min 255 dimmedAlpha)
        for x in 0 .. width - 1 do
            let i = x * 4
            let a = int row.[i + 3]
            let effective = a * s / 255
            let glass =
                if a <= 1 || backdrop.[i + 3] = 0uy || effective = 0 then None
                elif dimmed.[x] && a <= d then Some(min 255 (a * 255 / d), d * s / 255)
                elif s < 255 then Some(a, s)
                else None
            match glass with
            | Some(coverage, tint) ->
                for c in 0 .. 2 do
                    row.[i + c] <- byte ((int row.[i + c] * tint + int backdrop.[i + c] * (255 - tint) + 127) / 255)
                row.[i + 3] <- byte coverage
            | None -> row.[i + 3] <- byte effective

module private TabGlassNative =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type WallRect =
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int

    // shobjidl_core.h, in vtable order up to the last method used.
    [<ComImport; Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type IDesktopWallpaper =
        [<PreserveSig>]
        abstract SetWallpaper: [<MarshalAs(UnmanagedType.LPWStr)>] monitorID: string * [<MarshalAs(UnmanagedType.LPWStr)>] wallpaper: string -> int
        [<PreserveSig>]
        abstract GetWallpaper: [<MarshalAs(UnmanagedType.LPWStr)>] monitorID: string * wallpaper: byref<nativeint> -> int
        [<PreserveSig>]
        abstract GetMonitorDevicePathAt: monitorIndex: uint32 * monitorID: byref<nativeint> -> int
        [<PreserveSig>]
        abstract GetMonitorDevicePathCount: count: byref<uint32> -> int
        [<PreserveSig>]
        abstract GetMonitorRECT: [<MarshalAs(UnmanagedType.LPWStr)>] monitorID: string * displayRect: byref<WallRect> -> int
        [<PreserveSig>]
        abstract SetBackgroundColor: color: uint32 -> int
        [<PreserveSig>]
        abstract GetBackgroundColor: color: byref<uint32> -> int
        [<PreserveSig>]
        abstract SetPosition: position: int -> int
        [<PreserveSig>]
        abstract GetPosition: position: byref<int> -> int

    let clsidDesktopWallpaper = Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")

    let takeString (p: nativeint) =
        if p = IntPtr.Zero then ""
        else
            try Marshal.PtrToStringUni(p)
            finally Marshal.FreeCoTaskMem(p)

/// What the desktop background is, as far as the glass needs to know.
type private WallpaperMonitor = { bounds: Rectangle; path: string option; stamp: DateTime }
type private WallpaperState = { monitors: WallpaperMonitor list; position: int; background: Color }

/// A monitor's wallpaper, reduced and blurred, ready to be sampled.
type private GlassMonitor = { area: Rectangle; width: int; height: int; pixels: byte[] }

module TabGlass =
    let private sync = obj()
    // Published pixels are immutable. Readers hold ordinary managed references;
    // neither disposal nor GDI+ objects are shared between drawing threads.
    let mutable private cache : int * GlassMonitor list = -1, []
    let mutable private requested = 0
    let mutable private pending = 0
    let mutable private checkedAt = -3000L
    let mutable private generationValue = 0
    let private changedEvent = Event<unit>()
    let changed = changedEvent.Publish
    let private wake = new AutoResetEvent(false)
    let private clock = System.Diagnostics.Stopwatch.StartNew()
    let private recheckMs = 3000L
    let private blurRadius = 2

    let private trace (text: unit -> string) =
#if DEBUG
        System.Diagnostics.Debug.WriteLine("[TabGlass] " + text())
#else
        ()
#endif

    let generation() = Volatile.Read(&generationValue)
    let private notify() =
        Interlocked.Increment(&generationValue) |> ignore
        changedEvent.Trigger()

    // "Transparency effects" in Settings > Personalization > Colors. Missing
    // means the Windows default, on.
    let private transparencyOn() =
        try
            use key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            match (if isNull key then null else key.GetValue("EnableTransparency")) with
            | :? int as value -> value <> 0
            | _ -> true
        with _ -> true

    let private readState() : WallpaperState option =
        try
            let instance = Activator.CreateInstance(Type.GetTypeFromCLSID(TabGlassNative.clsidDesktopWallpaper))
            try
                let wallpaper = instance :?> TabGlassNative.IDesktopWallpaper
                let mutable count = 0u
                let mutable position = TabGlassMath.Fill
                let mutable color = 0u
                if wallpaper.GetMonitorDevicePathCount(&count) < 0 || not (TabGlassMath.validMonitorCount count) ||
                   wallpaper.GetPosition(&position) < 0 || wallpaper.GetBackgroundColor(&color) < 0 ||
                   position < TabGlassMath.Center || position > TabGlassMath.Span || position = TabGlassMath.Tile then None
                else
                    let mutable geometryFailed = false
                    let monitors =
                        [ for index in 0u .. count - 1u do
                            let mutable idPtr = IntPtr.Zero
                            let idResult = wallpaper.GetMonitorDevicePathAt(index, &idPtr)
                            let id = TabGlassNative.takeString idPtr
                            let mutable rect = TabGlassNative.WallRect()
                            if idResult < 0 || id = "" || wallpaper.GetMonitorRECT(id, &rect) < 0 then
                                geometryFailed <- true
                            elif rect.right > rect.left && rect.bottom > rect.top then
                                let mutable pathPtr = IntPtr.Zero
                                let result = wallpaper.GetWallpaper(id, &pathPtr)
                                let value = TabGlassNative.takeString pathPtr
                                let path = if result >= 0 then Some value else None
                                let stamp =
                                    try
                                        match path with
                                        | Some path when path <> "" -> File.GetLastWriteTimeUtc(path)
                                        | _ -> DateTime.MinValue
                                    with _ -> DateTime.MinValue
                                yield { bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom); path = path; stamp = stamp } ]
                    let background = Color.FromArgb(int (color &&& 0xFFu), int ((color >>> 8) &&& 0xFFu), int ((color >>> 16) &&& 0xFFu))
                    // Span needs every monitor's geometry, including failed wallpapers.
                    if geometryFailed || List.isEmpty monitors then None
                    else Some { monitors = monitors; position = position; background = background }
            finally
                if Marshal.IsComObject(instance) then Marshal.ReleaseComObject(instance) |> ignore
        with _ -> None

    let private buildMonitor (wall: WallpaperState) (virtualBounds: Rectangle) (picture: Image option) (m: WallpaperMonitor) : GlassMonitor option =
        if not (TabGlassMath.validImageSize m.bounds.Width m.bounds.Height) then None
        else
            let sw, sh = TabGlassMath.reducedSize (m.bounds.Width, m.bounds.Height)
            let reduced = new Bitmap(sw, sh, PixelFormat.Format32bppArgb)
            try
                reduced.SetResolution(96.0f, 96.0f)
                (use g = Graphics.FromImage(reduced)
                 g.Clear(wall.background)
                 g.InterpolationMode <- InterpolationMode.HighQualityBicubic
                 g.PixelOffsetMode <- PixelOffsetMode.Half
                 use attributes = new ImageAttributes()
                 attributes.SetWrapMode(WrapMode.TileFlipXY)
                 picture |> Option.iter (fun picture ->
                    let frame =
                        if wall.position = TabGlassMath.Span then
                            float (virtualBounds.X - m.bounds.X), float (virtualBounds.Y - m.bounds.Y),
                            float virtualBounds.Width, float virtualBounds.Height
                        else 0.0, 0.0, float m.bounds.Width, float m.bounds.Height
                    let x, y, w, h = TabGlassMath.placement wall.position frame (picture.Width, picture.Height)
                    let x, y, w, h = TabGlassMath.reducedPlacement (m.bounds.Width, m.bounds.Height) (x, y, w, h)
                    let dest = RectangleF(float32 x, float32 y, float32 w, float32 h)
                    g.DrawImage(picture, Rectangle.Round(dest), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes)))
                let pixels : byte[] = Array.zeroCreate (sw * sh * 4)
                let data = reduced.LockBits(Rectangle(0, 0, sw, sh), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb)
                try
                    let stride = sw * 4
                    for row in 0 .. sh - 1 do
                        Marshal.Copy(data.Scan0 + nativeint (row * data.Stride), pixels, row * stride, stride)
                    TabGlassMath.blur pixels sw sh blurRadius
                    // The desktop is opaque; an alpha left over from the
                    // picture must not read as "no backdrop" later.
                    for i in 3 .. 4 .. pixels.Length - 1 do pixels.[i] <- 255uy
                finally reduced.UnlockBits(data)
                reduced.Dispose()
                Some { area = m.bounds; width = sw; height = sh; pixels = pixels }
            with _ ->
                reduced.Dispose()
                None

    let private rebuild (wall: WallpaperState) =
        let virtualBounds = wall.monitors |> List.map (fun m -> m.bounds) |> List.reduce (fun a b -> Rectangle.Union(a, b))
        // Keep at most one source image alive, with no full-size Bitmap copy
        // or file-sized byte array. The stream stays open through rendering.
        wall.monitors |> List.choose (fun m ->
            try
                match m.path with
                | None -> None
                | Some "" -> buildMonitor wall virtualBounds None m
                | Some path ->
                    use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    if not (TabGlassMath.validFileSize stream.Length) then None
                    else
                        use image = Image.FromStream(stream, false, false)
                        if not (TabGlassMath.validImageSize image.Width image.Height) then None
                        else buildMonitor wall virtualBounds (Some image) m
            with _ -> None)

    let private worker = lazy (
        let run() =
            let mutable state : WallpaperState option = None
            let mutable built : GlassMonitor list = []
            let mutable completed = -1
            let mutable retry = false
            while true do
                wake.WaitOne() |> ignore
                let revision = Volatile.Read(&requested)
                try
                    let wanted = transparencyOn() && not System.Windows.Forms.SystemInformation.HighContrast
                    let wall = if wanted then readState() else None
                    if revision <> completed || wall <> state || retry then
                        // Fall back while preparing a replacement. Never perform
                        // COM, file I/O or graphics work under the cache lock.
                        lock sync (fun () -> cache <- -1, [])
                        notify()
                        built <- match wall with Some wall -> rebuild wall | None -> []
                        retry <- wall |> Option.exists (fun w -> built.Length <> w.monitors.Length)
                        trace (fun () ->
                            match wall with
                            | Some wall ->
                                sprintf "state=on monitors=%d glass=%d position=%d wallpapers=%d"
                                    wall.monitors.Length built.Length wall.position
                                    (wall.monitors |> List.filter (fun m -> m.path |> Option.exists ((<>) "")) |> List.length)
                            | None ->
                                if wanted then "state=off reason=no-wallpaper-information"
                                else "state=off reason=transparency-effects-off-or-high-contrast")
                        state <- wall
                        completed <- revision
                        if revision = Volatile.Read(&requested) then
                            lock sync (fun () -> cache <- revision, built)
                            notify()
                with _ ->
                    state <- None
                    retry <- true
                    lock sync (fun () -> cache <- -1, [])
                    notify()
                Interlocked.Exchange(&checkedAt, clock.ElapsedMilliseconds) |> ignore
                Interlocked.Exchange(&pending, 0) |> ignore
                // An invalidation during decoding must not publish stale pixels
                // or get lost when the worker goes back to sleep.
                if revision <> Volatile.Read(&requested) && Interlocked.CompareExchange(&pending, 1, 0) = 0 then
                    wake.Set() |> ignore
        let thread = new Thread(ThreadStart(run), IsBackground = true, Name = "Wallpaper glass")
        thread.SetApartmentState(ApartmentState.STA)
        thread.Start())

    /// Called before deciding whether a strip's presentation can be reused.
    /// No timer: periodic checks and retries only run when a strip updates.
    let refresh() =
        if clock.ElapsedMilliseconds - Interlocked.Read(&checkedAt) >= recheckMs &&
           Interlocked.CompareExchange(&pending, 1, 0) = 0 then
            worker.Force()
            wake.Set() |> ignore

    /// Nonblocking even while a source image is being decoded.
    let invalidate() =
        Interlocked.Increment(&requested) |> ignore
        Interlocked.Increment(&generationValue) |> ignore
        if Interlocked.CompareExchange(&pending, 1, 0) = 0 then
            worker.Force()
            wake.Set() |> ignore

    // The blurred wallpaper under a screen rectangle, as a bitmap of its size;
    // transparent where no monitor with glass lies under it. None when there
    // is none at all.
    let private backdropFor (monitors: GlassMonitor list) (screen: Rectangle) : Bitmap option =
        let under = monitors |> List.filter (fun m -> m.area.IntersectsWith(screen))
        if List.isEmpty under then None
        else
            let backdrop = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb)
            try
                backdrop.SetResolution(96.0f, 96.0f)
                use g = Graphics.FromImage(backdrop)
                g.Clear(Color.Transparent)
                g.InterpolationMode <- InterpolationMode.Bilinear
                g.PixelOffsetMode <- PixelOffsetMode.Half
                g.CompositingMode <- CompositingMode.SourceCopy
                use attributes = new ImageAttributes()
                attributes.SetWrapMode(WrapMode.TileFlipXY)
                for m in under do
                    let part = Rectangle.Intersect(m.area, screen)
                    let kx = float32 m.width / float32 m.area.Width
                    let ky = float32 m.height / float32 m.area.Height
                    let dest = Rectangle(part.X - screen.X, part.Y - screen.Y, part.Width, part.Height)
                    let pinned = GCHandle.Alloc(m.pixels, GCHandleType.Pinned)
                    try
                        use picture = new Bitmap(m.width, m.height, m.width * 4, PixelFormat.Format32bppArgb, pinned.AddrOfPinnedObject())
                        g.DrawImage(picture, dest,
                                    float32 (part.X - m.area.X) * kx, float32 (part.Y - m.area.Y) * ky,
                                    float32 part.Width * kx, float32 part.Height * ky,
                                    GraphicsUnit.Pixel, attributes)
                    finally pinned.Free()
                Some backdrop
            with _ ->
                backdrop.Dispose()
                None

    /// The strip image at `location` (screen pixels) with frosted glass under
    /// its translucent tabs, to be shown at opacity 255. `windowAlpha` is the
    /// opacity the image was to be shown at; `dimmed` lists the rectangles of
    /// the image holding tabs drawn faded. None when nothing is translucent or
    /// the glass cannot be drawn: the caller shows the image as before.
    let present (image: Img) (location: Pt) (windowAlpha: byte) (dimmed: unit -> Rectangle list) : Img option =
        if windowAlpha = 0uy then None
        else
            try
                let rects = dimmed()
                if windowAlpha = 255uy && List.isEmpty rects then None
                else
                    PerfTrace.time "glass.present" (fun () ->
                        let width, height = image.width, image.height
                        let revision, monitors = lock sync (fun () -> cache)
                        let backdrop =
                            if revision = Volatile.Read(&requested) then
                                backdropFor monitors (Rectangle(location.x, location.y, width, height))
                            else None
                        match backdrop with
                        | None ->
                            PerfTrace.count "glassUnavailable"
                            None
                        | Some backdrop ->
                            use backdrop = backdrop
                            let result = Img(image.size)
                            try
                                let whole = Rectangle(0, 0, width, height)
                                let source = image.bitmap.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb)
                                try
                                    let under = backdrop.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb)
                                    try
                                        let target = result.bitmap.LockBits(whole, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
                                        try
                                            // Row by row, so no buffer lands on the large object heap.
                                            let stride = width * 4
                                            let row : byte[] = Array.zeroCreate stride
                                            let back : byte[] = Array.zeroCreate stride
                                            let mask : bool[] = Array.zeroCreate width
                                            for y in 0 .. height - 1 do
                                                Marshal.Copy(source.Scan0 + nativeint (y * source.Stride), row, 0, stride)
                                                Marshal.Copy(under.Scan0 + nativeint (y * under.Stride), back, 0, stride)
                                                Array.Clear(mask, 0, width)
                                                for r in rects do
                                                    if y >= r.Top && y < r.Bottom then
                                                        for x in max 0 r.Left .. min width r.Right - 1 do mask.[x] <- true
                                                TabGlassMath.composeRow row back mask width (int windowAlpha) VirtualDesktopGroups.dimmedAlpha
                                                Marshal.Copy(row, 0, target.Scan0 + nativeint (y * target.Stride), stride)
                                        finally result.bitmap.UnlockBits(target)
                                    finally backdrop.UnlockBits(under)
                                finally image.bitmap.UnlockBits(source)
                                Some result
                            with _ ->
                                result.bitmap.Dispose()
                                reraise())
            with _ ->
                PerfTrace.count "glassFailed"
                None
