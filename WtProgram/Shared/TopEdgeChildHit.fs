namespace Bemo

open System
open System.Runtime.InteropServices

/// Read-only top-edge sampling, shared by the guard and diagnostic tests.
module TopEdgeChildHit =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NativeRect =
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int

    type Box = { left: int; top: int; right: int; bottom: int }
    type Target = { hwnd: nativeint; bounds: Box }
    type EnumCallback = delegate of nativeint * nativeint -> bool

    [<DllImport("user32.dll")>]
    extern bool private EnumChildWindows(nativeint owner, EnumCallback callback, nativeint parameter)
    [<DllImport("user32.dll")>]
    extern bool private IsWindowVisible(nativeint hwnd)
    [<DllImport("user32.dll", EntryPoint = "GetWindowLongW")>]
    extern int private GetWindowLong(nativeint hwnd, int index)
    [<DllImport("user32.dll")>]
    extern bool private GetWindowRect(nativeint hwnd, NativeRect& bounds)
    [<DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")>]
    extern nativeint private NativeSendMessageTimeout(nativeint hwnd, uint32 message, nativeint wparam, nativeint lparam, uint32 flags, uint32 timeout, nativeint& result)

    let isVisible hwnd = IsWindowVisible hwnd

    let private sendHit hwnd packed timeout =
#if DEBUG
        let call = Bemo.Win32.GroupCallTrace.Begin("SendMessageTimeout", hwnd)
#endif
        try
            let mutable result = IntPtr.Zero
            if NativeSendMessageTimeout(hwnd, 0x0084u (* WM_NCHITTEST *), IntPtr.Zero,
                                        nativeint packed, 0x0002u (* SMTO_ABORTIFHUNG *),
                                        uint32 timeout, &result) = IntPtr.Zero then None
            else Some(result.ToInt32())
        finally
#if DEBUG
            Bemo.Win32.GroupCallTrace.End(call)
#else
            ()
#endif

    let rect hwnd =
        let mutable r = NativeRect()
        if GetWindowRect(hwnd, &r) then
            Some { left=r.left; top=r.top; right=r.right; bottom=r.bottom }
        else None

    /// Intersect first: a child may return HTTOP even outside its rectangle.
    let sampleColumn (owner: Box) (child: Box) =
        let left, right = max owner.left child.left, min owner.right child.right
        if left >= right || child.top > owner.top || child.bottom <= owner.top then None
        else Some(left + (right - left) / 2, min owner.bottom child.bottom)

    let combine fallback ownerHeight childHeights =
        let height = List.fold max ownerHeight childHeights
        if height > 0 then height else fallback

    /// No messages are sent while collecting the cache signature.
    let children owner bounds =
        let found = ResizeArray<Target>()
        let callback = EnumCallback(fun hwnd _ ->
            if IsWindowVisible hwnd && (GetWindowLong(hwnd, -16 (* GWL_STYLE *)) &&& 0x40000000 (* WS_CHILD *)) <> 0 then
                match rect hwnd with
                | Some r when (sampleColumn bounds r).IsSome -> found.Add { hwnd=hwnd; bounds=r }
                | _ -> ()
            true)
        EnumChildWindows(owner, callback, IntPtr.Zero) |> ignore
        found |> Seq.toList

    let signature (owner: Box) targets =
        targets |> List.map (fun t ->
            t.hwnd, t.bounds.left-owner.left, t.bounds.top-owner.top,
            min 28 (t.bounds.bottom-owner.top)) |> List.sort

    type Measurement = { height: int; complete: bool }

    // Stop at the first non-HTTOP row, with every point inside both boxes.
    // A window that did not answer will not answer the next row either;
    // asking all twenty-eight rows would hold this thread unnecessarily.
    let measureColumn (owner: Box) (target: Target) hitAt =
        match sampleColumn owner target.bounds with
        | None -> { height=0; complete=true }
        | Some(x, bottom) ->
            let reach = min 28 (min (max 1 ((owner.bottom-owner.top)/2)) (bottom-owner.top))
            let mutable height = 0
            let mutable asking, complete = true, true
            while asking && height < reach do
                let answer = try hitAt target.hwnd x (owner.top+height) with _ -> None
                match answer with
                | Some 12 (* HTTOP *) -> height <- height+1
                | Some _ -> asking <- false
                | None -> asking <- false; complete <- false
            { height=height; complete=complete }

    // Each column owns its budget. The guard advances at most the parent and
    // one child per update, so neither can spend the other's allowance.
    let measureTargetWith elapsed send bounds target =
        let mutable remaining = 28
        let hitAt hwnd x y =
            let timeLeft = 100 - elapsed ()
            if remaining <= 0 || timeLeft <= 0 then None
            else
                remaining <- remaining-1
                let packed = ((y &&& 0xffff) <<< 16) ||| (x &&& 0xffff)
                send hwnd packed (min 60 timeLeft)
        measureColumn bounds target hitAt

    let measureTarget bounds target =
        let timer = Diagnostics.Stopwatch.StartNew()
        measureTargetWith (fun () -> int timer.ElapsedMilliseconds) sendHit bounds target

    // Partial answers are useful while retrying, but never become a completed
    // cache entry. Rotate children even when one hangs, retaining finished ones.
    type Scan() =
        let mutable own : Measurement option = None
        let results = Collections.Generic.Dictionary<nativeint, Measurement>()
        let mutable nextChild = 0
        member this.Advance(owner, bounds, targets: Target list, fallback, sample) =
            let keep previous current =
                if current.complete then current
                else { current with height=max (previous |> Option.map (fun p -> p.height) |> Option.defaultValue 0) current.height }
            if own |> Option.forall (fun r -> not r.complete) then
                own <- Some(keep own (sample bounds { hwnd=owner; bounds=bounds }))
            let pending = targets |> List.filter (fun t ->
                not(results.ContainsKey t.hwnd) || not results.[t.hwnd].complete)
            if not pending.IsEmpty then
                let target = pending.[nextChild % pending.Length]
                nextChild <- nextChild+1
                let previous = if results.ContainsKey target.hwnd then Some results.[target.hwnd] else None
                results.[target.hwnd] <- keep previous (sample bounds target)
            let values = targets |> List.map (fun t ->
                if results.ContainsKey t.hwnd then results.[t.hwnd] else { height=0; complete=false })
            let parent = own.Value
            // Nothing named the border: self-resizing windows such as LINE
            // need the system metrics. Any positive answer keeps its own size.
            { height=combine fallback parent.height (values |> List.map (fun r -> r.height))
              complete=parent.complete && List.forall (fun r -> r.complete) values }

    // Diagnostic convenience: production advances only once per update.
    let measure owner bounds targets fallback =
        let scan = Scan()
        let mutable result = { height=0; complete=false }
        for _ in 0 .. max 0 (List.length targets - 1) do
            result <- scan.Advance(owner, bounds, targets, fallback, measureTarget)
        result

    let completedEntry key result =
        if result.complete then Some(key, result.height) else None

    let reuseChildren now last sameGeometry = sameGeometry && now-last < 1000L
    let retryDue now last = now-last >= 1000L
