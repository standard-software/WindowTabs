namespace Bemo

open System
open System.Collections.Generic
open System.Runtime.InteropServices

module WindowFrameMargin =
    type Bounds = { x: int; y: int; width: int; height: int }
    type Margin = { top: int; left: int; right: int; bottom: int }
    let zero = { top = 0; left = 0; right = 0; bottom = 0 }

    // Input contains only visible, directly owned windows from other processes. Corner pieces may
    // shorten an edge piece by up to the thickness limit at either end, but a
    // small popup must not count as a full side. All coordinates are pixels.
    let detect dpi (owner: Bounds) (frames: Bounds list) =
        let limit = max 1 ((32 * max 96 dpi + 95) / 96)
        let spans start length edge extent =
            length > 0 && start <= edge + limit && start + length >= edge + extent - limit &&
            length * 2 >= extent
        let horizontal (r: Bounds) = spans r.x r.width owner.x owner.width
        let vertical (r: Bounds) = spans r.y r.height owner.y owner.height
        // A frame may overlap the owner by up to two logical pixels. Only
        // its outside portion contributes margin; gaps and inside-only pieces do not.
        let overlapLimit = max 1 ((2 * max 96 dpi + 95) / 96)
        let outside thickness extent =
            thickness > 0 && thickness <= limit && extent > 0 &&
            thickness - extent >= 0 && thickness - extent <= overlapLimit
        if owner.width <= 0 || owner.height <= 0 then zero
        else
            frames |> List.fold (fun m r ->
                { top = if outside r.height (owner.y - r.y) && horizontal r
                        then max m.top (owner.y - r.y) else m.top
                  bottom = if outside r.height (r.y + r.height - owner.y - owner.height) && horizontal r
                           then max m.bottom (r.y + r.height - owner.y - owner.height) else m.bottom
                  left = if outside r.width (owner.x - r.x) && vertical r
                         then max m.left (owner.x - r.x) else m.left
                  right = if outside r.width (r.x + r.width - owner.x - owner.width) && vertical r
                          then max m.right (r.x + r.width - owner.x - owner.width) else m.right }) zero

    let scale fromDpi toDpi (margin: Margin) =
        let px value = int (Math.Round(float value * float (max 1 toDpi) / float (max 1 fromDpi), MidpointRounding.AwayFromZero))
        px margin.top, px margin.left, px margin.right, px margin.bottom

    // Missing sides retain their original measurement and DPI independently.
    type Measurement = { pixels: int; dpi: int }
    type Sticky = { top: Measurement; left: Measurement; right: Measurement; bottom: Measurement }
    let emptySticky : Sticky =
        let empty = { pixels = 0; dpi = 96 }
        { top = empty; left = empty; right = empty; bottom = empty }
    let remember dpi (detected: Margin) (previous: Sticky) : Sticky =
        let side pixels previous = if pixels > 0 then { pixels = pixels; dpi = dpi } else previous
        { top = side detected.top previous.top; left = side detected.left previous.left
          right = side detected.right previous.right; bottom = side detected.bottom previous.bottom }
    let readSticky dpi (value: Sticky) =
        let side m = int (Math.Round(float m.pixels * float (max 1 dpi) / float (max 1 m.dpi), MidpointRounding.AwayFromZero))
        side value.top, side value.left, side value.right, side value.bottom

    type private Entry = { mutable value: Sticky; mutable users: int }
    let private gate = obj()
    let private shared = Dictionary<nativeint, Entry>()

    module private Native =
        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type RECT =
            val mutable left: int
            val mutable top: int
            val mutable right: int
            val mutable bottom: int
        [<DllImport("user32.dll")>]
        extern bool GetWindowRect(nativeint hwnd, RECT& rect)
        [<DllImport("user32.dll")>]
        extern nativeint GetWindow(nativeint hwnd, uint32 command)
        [<DllImport("user32.dll")>]
        extern nativeint GetTopWindow(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsWindowVisible(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsWindow(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsIconic(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsZoomed(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern uint32 GetDpiForWindow(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern uint32 GetWindowThreadProcessId(nativeint hwnd, uint32& processId)
        [<DllImport("kernel32.dll")>]
        extern uint32 GetCurrentProcessId()

    let private currentProcessId = Native.GetCurrentProcessId()
    let isExternalFrame hwnd =
        let mutable processId = 0u
        Native.GetWindowThreadProcessId(hwnd, &processId) <> 0u &&
        processId <> 0u && processId <> currentProcessId

    let private bounds hwnd =
        let mutable r = Native.RECT()
        if Native.GetWindowRect(hwnd, &r) then
            Some { x = r.left; y = r.top; width = r.right - r.left; height = r.bottom - r.top }
        else None

    // Per-group subscriptions share the HWND's lifetime sample. Removing one
    // group cannot erase margins while another group still contains the HWND.
    type Cache() =
        let members = Dictionary<nativeint, Entry>()
        let scanned = Dictionary<nativeint, int64>()
        member this.Attach(hwnd) =
            lock gate (fun () ->
                if not (members.ContainsKey hwnd) then
                    let entry =
                        match shared.TryGetValue hwnd with
                        | true, value -> value
                        | _ ->
                            let value = { value = emptySticky; users = 0 }
                            shared.[hwnd] <- value
                            value
                    entry.users <- entry.users + 1
                    members.[hwnd] <- entry)
        member this.Get(hwnd, dpi) =
            lock gate (fun () ->
                match members.TryGetValue hwnd with
                | true, entry -> readSticky dpi entry.value
                | _ -> 0, 0, 0, 0)
        member this.Remove(hwnd) =
            lock gate (fun () ->
                match members.TryGetValue hwnd with
                | true, entry ->
                    members.Remove hwnd |> ignore
                    entry.users <- entry.users - 1
                    match shared.TryGetValue hwnd with
                    | true, current when (entry.users = 0 || not (Native.IsWindow hwnd)) && obj.ReferenceEquals(current, entry) ->
                        entry.value <- emptySticky
                        shared.Remove hwnd |> ignore
                    | _ -> ()
                | _ -> ())
            scanned.Remove hwnd |> ignore
        member this.Clear() =
            for hwnd in List.ofSeq members.Keys do this.Remove(hwnd)
        member this.Invalidate(hwnd) = scanned.Remove hwnd |> ignore
        member this.Refresh(hwnd, excluded: nativeint -> bool) =
            let now = Diagnostics.Stopwatch.GetTimestamp()
            let due =
                match scanned.TryGetValue hwnd with
                | true, previous -> now - previous >= Diagnostics.Stopwatch.Frequency / 20L
                | _ -> true
            if not (Native.IsWindow hwnd) then
                lock gate (fun () ->
                    match shared.TryGetValue hwnd with
                    | true, entry -> entry.value <- emptySticky; shared.Remove hwnd |> ignore
                    | _ -> ())
                this.Remove(hwnd)
                false
            elif not due || not (Native.IsWindowVisible hwnd) || Native.IsIconic hwnd || Native.IsZoomed hwnd then
                // Configured margins used to survive hidden/minimized/maximized
                // states. Preserve the last normal sample for restoration.
                false
            else
                this.Attach(hwnd)
                scanned.[hwnd] <- now
                match bounds hwnd with
                | None -> false
                | Some owner ->
                    let rec walk candidate remaining frames =
                        if candidate = 0n then Some frames
                        elif remaining = 0 then None
                        else
                            let frames =
                                if not (excluded candidate) && Native.GetWindow(candidate, 4u) = hwnd && Native.IsWindowVisible candidate && isExternalFrame candidate then
                                    match bounds candidate with Some r -> r :: frames | None -> frames
                                else frames
                            walk (Native.GetWindow(candidate, 2u)) (remaining - 1) frames
                    // Owned frame pieces can sit behind their owner after a reorder.
                    // Search both sides of the owner in the top-level order.
                    match walk (Native.GetTopWindow(0n)) 4096 [] with
                    | None -> false // Incomplete order is not evidence of zero margin.
                    | Some frames ->
                        let dpi = try max 96 (int (Native.GetDpiForWindow hwnd)) with _ -> 96
                        let margin = detect dpi owner frames
                        let before = this.Get(hwnd, dpi)
                        lock gate (fun () ->
                            let entry = members.[hwnd]
                            entry.value <- remember dpi margin entry.value)
                        let changed = before <> this.Get(hwnd, dpi)
#if DEBUG
                        if changed then Diagnostics.Debug.WriteLine(sprintf "[WindowFrameMargin] hwnd=%X margin=%A dpi=%d" (int64 hwnd) margin dpi)
#endif
                        changed
