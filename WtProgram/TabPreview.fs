namespace Bemo

open System
open System.Drawing
open System.Runtime.InteropServices
open System.Windows.Forms

module private ThumbnailNative =
    [<DllImport("user32.dll")>]
    extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint32 color, byte alpha, uint32 flags)
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NativeRect =
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Properties =
        val mutable flags: uint32
        val mutable destination: NativeRect
        val mutable source: NativeRect
        val mutable opacity: byte
        val mutable visible: int
        val mutable clientOnly: int
    [<DllImport("dwmapi.dll")>]
    extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, IntPtr& thumbnail)
    [<DllImport("dwmapi.dll")>]
    extern int DwmUnregisterThumbnail(IntPtr thumbnail)
    [<DllImport("dwmapi.dll")>]
    extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, Size& size)
    [<DllImport("dwmapi.dll")>]
    extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, Properties& properties)

type TabPreview() =
    inherit Form(FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                 StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None)
    let mutable thumbnail = IntPtr.Zero
    let mutable padding = 0
    let mutable border = SystemColors.WindowFrame
    let release() =
        if thumbnail <> IntPtr.Zero then
            ThumbnailNative.DwmUnregisterThumbnail(thumbnail) |> ignore
            thumbnail <- IntPtr.Zero
    override this.ShowWithoutActivation = true
    override this.CreateParams =
        let p = base.CreateParams
        p.ExStyle <- p.ExStyle ||| 0x08000000 ||| 0x80 ||| 0x20 ||| 0x80000
        p
    override this.WndProc(message: Message byref) =
        if message.Msg = 0x84 then message.Result <- IntPtr(-1)
        elif message.Msg = 0x21 then message.Result <- IntPtr(3)
        else base.WndProc(&message)
    override this.OnPaint(e) =
        base.OnPaint(e)
        use pen = new Pen(border)
        e.Graphics.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1)
    member this.Dismiss() =
        this.Hide()
        release()
    member private this.HideUnavailable(reason: string) =
#if DEBUG
        DragTrace.log (fun () -> "[preview] hidden reason=" + reason)
#else
        ignore reason
#endif
        this.Dismiss()
    member this.ValidateSource(hwnd: IntPtr) =
        if thumbnail <> IntPtr.Zero then
            let mutable size = Size.Empty
            if (OS()).windowFromHwnd(hwnd).isMinimized then
                this.HideUnavailable("minimized")
            elif ThumbnailNative.DwmQueryThumbnailSourceSize(thumbnail, &size) <> 0 then
                this.HideUnavailable("source-size-query")
            elif size.Width <= 0 || size.Height <= 0 then
                this.HideUnavailable("source-size-empty")
    member this.ShowPreview(hwnd: IntPtr, anchor: Pt, tabEdge: TabEdges.Edge, tooltip: Rectangle, edge: int) =
        this.Dismiss()
        let scale = Dpi.scaleForPoint anchor
        padding <- Dpi.px scale 4
        let dark = Services.settings.root.getBool("EnableDarkMode").def(false)
        this.BackColor <- if dark then DarkMode.darkSurface else SystemColors.Window
        border <- if dark then DarkMode.darkBorder else SystemColors.WindowFrame
        let work = (Mon.nearestFromPoint anchor).Value.workRect
        let mutable source = Size.Empty
        if (OS()).windowFromHwnd(hwnd).isMinimized then
            this.HideUnavailable("minimized")
        elif ThumbnailNative.DwmRegisterThumbnail(this.Handle, hwnd, &thumbnail) <> 0 then
            this.HideUnavailable("thumbnail-registration")
        elif ThumbnailNative.DwmQueryThumbnailSourceSize(thumbnail, &source) <> 0 then
            this.HideUnavailable("source-size-query")
        elif source.Width <= 0 || source.Height <= 0 then
            this.HideUnavailable("source-size-empty")
        else
            let width, height = TabPreviewPolicy.fit source.Width source.Height (Dpi.px scale edge)
                                    (work.width - 2 * padding) (work.height - 2 * padding)
            this.Size <- Size(width + 2 * padding, height + 2 * padding)
            match TabPreviewPolicy.besideTooltip tabEdge (work.left, work.top, work.right, work.bottom)
                      (tooltip.Left, tooltip.Top, tooltip.Right, tooltip.Bottom) padding this.Width this.Height with
            | None -> this.HideUnavailable("tooltip-space")
            | Some(x, y) ->
                let mutable rect = Unchecked.defaultof<ThumbnailNative.NativeRect>
                rect.left <- padding
                rect.top <- padding
                rect.right <- padding + width
                rect.bottom <- padding + height
                let mutable properties = Unchecked.defaultof<ThumbnailNative.Properties>
                properties.flags <- 0x1Du
                properties.destination <- rect
                properties.opacity <- 255uy
                properties.visible <- 1
                if ThumbnailNative.DwmUpdateThumbnailProperties(thumbnail, &properties) <> 0 then
                    this.HideUnavailable("thumbnail-update")
                else
                    this.Location <- Point(x, y)
                    ThumbnailNative.SetLayeredWindowAttributes(this.Handle, 0u, 255uy, 2u) |> ignore
                    this.Show()
                    (OS()).windowFromHwnd(this.Handle).makeTopMost()
                    this.Invalidate()
    override this.Dispose(disposing) =
        release()
        base.Dispose(disposing)
