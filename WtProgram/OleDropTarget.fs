namespace Bemo
open System
open System.Collections
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.Reflection
open System.IO
open System.Windows.Forms
open Bemo.Win32.Forms

module private OleNative =
    [<System.Runtime.InteropServices.DllImport("ole32.dll")>]
    extern int RevokeDragDrop(IntPtr hwnd)
    [<System.Runtime.InteropServices.DllImport("ole32.dll")>]
    extern void OleUninitialize()

// A strip's window as a place to drop files on a tab (copied or moved into
// the folder the tab's window shows). The top strip's by default; the strip
// of another edge passes its window and how to find the tab under a point
// of it.
type OleDropTarget(ts:TabStrip, hwnd: IntPtr, tryHit: Pt -> Option<Tab*TabPart>) as this=
    let Cell = CellScope()
    let os = OS()
    let window = os.windowFromHwnd(hwnd)
    let rButtonDown = Cell.create(false)
    let lastTabHwndCell = Cell.create(None)
    let mutable initialized = false
    let mutable registered = false

    do
        
        let result = Ole2Api.OleInitialize(IntPtr.Zero).ToInt64()
        initialized <- result = 0L || result = 1L
        registered <- Ole2Api.RegisterDragDrop(window.hwnd, this) = IntPtr.Zero

    new(ts: TabStrip) = OleDropTarget(ts, ts.hwnd, fun pt -> ts.tryHit(pt))

    /// Take the window off as a place to drop on, before it is destroyed.
    member this.revoke() =
        try
            if registered then
                registered <- false
                OleNative.RevokeDragDrop(window.hwnd) |> ignore
        finally
            if initialized then
                initialized <- false
                OleNative.OleUninitialize()

    member this.dragEnd() =
        rButtonDown.value <- false
        lastTabHwndCell.set(None)

    interface IOleDropTarget with
        member x.OleDragEnter(pDataObj, grfKeyState, pt, pdwEffect) =
            0

        member x.OleDragOver(grfKeyState, pt, pdwEffect) = 
            //need to capture this here because it will be already released in the OleDrop handler
            rButtonDown.value <- grfKeyState.hasFlag(MouseMessageKeyStateMask.MK_RBUTTON)

            let ptScreen = Pt(pt.x, pt.y)
            let pt = window.ptToClient(Pt(pt.x, pt.y))

            let tabHwnd = tryHit(pt).map(fun(Tab(hwnd),part) -> hwnd)
            let effect = tabHwnd.map <| fun hwnd ->
                let shellFolder = Shell.getShellFolder hwnd
                if shellFolder.IsSome then 
                    if grfKeyState.hasFlag(MouseMessageKeyStateMask.MK_CONTROL) then
                        int(DragDropEffects.Copy)
                    else
                        int(DragDropEffects.Move)
                else 
                    int(DragDropEffects.None)
            let effect = effect.def(int(DragDropEffects.None))
            if lastTabHwndCell.value <> tabHwnd then
                lastTabHwndCell.set(tabHwnd)
                tabHwnd.iter <| fun hwnd ->
                    let window = os.windowFromHwnd(hwnd)
                    //setForegroundWindow will fail sometimes if a key is pressed during DragOver
                    //like CTRL. we will be unable to call setForeground until a new DragEnter is generated
                    window.setForegroundOrRestore(false)
                    window.bringToTop()
            pdwEffect <- effect
            0

        member x.OleDragLeave() = 
            this.dragEnd()
            0

        member x.OleDrop(pDataObj, grfKeyState, pt, pdwEffect) = 
            let initialEffect = pdwEffect
            let ptScreen = Pt(pt.x, pt.y)
            let pt = window.ptToClient(ptScreen)
            tryHit(pt).iter <| fun(Tab(hwnd),part) ->
                let shellFolder = Shell.getShellFolder hwnd
                Shell.getShellFolder(hwnd).iter <| fun shellFolder ->
                    let files = List2(OleHelper.QueryFiles(pDataObj))
                    let shellOp op = fun() ->
                        files.iter <| fun file ->
                            op(file)
                    let copy = shellOp <| fun file -> shellFolder.CopyHere(file, null)
                    let move = shellOp <| fun file -> shellFolder.MoveHere(file, null)
                    if rButtonDown.value then
                        let darkModeEnabled =
                            try
                                let json = Services.settings.root
                                match json.getBool("EnableDarkMode") with
                                | Some(value) -> value
                                | None -> false
                            with | _ -> false
                        // Text-only menu, but the scale still has to be the
                        // menu's own monitor so a future item with a bitmap
                        // gets it right by construction.
                        Win32Menu.show window.hwnd ptScreen (Dpi.scaleForPoint ptScreen) (List2([
                            CmiRegular({
                                text = "Copy"
                                image = None
                                flags = List2()
                                click = copy
                            })
                            CmiRegular({
                                text = "Move"
                                image = None
                                flags = List2()
                                click = move
                            })
                            CmiSeparator
                            CmiRegular({
                                text = "Cancel"
                                image = None
                                flags = List2()
                                click = fun() -> ()
                            })
                        ])) darkModeEnabled
                    elif grfKeyState.hasFlag(MouseMessageKeyStateMask.MK_CONTROL) then
                        copy()
                    else
                        move()
            this.dragEnd()
            0