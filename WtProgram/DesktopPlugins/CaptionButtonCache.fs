namespace Bemo

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open System.Windows.Automation

module private CaptionButtonNative =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Rect =
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int
    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(IntPtr hwnd, Rect& rect)
    [<DllImport("user32.dll")>]
    extern IntPtr SetThreadDpiAwarenessContext(IntPtr context)

    [<DllImport("user32.dll")>]
    extern uint32 GetDpiForWindow(IntPtr hwnd)

    let bounds hwnd =
        let mutable rect = Rect()
        if GetWindowRect(hwnd, &rect) then
            Some ({ left = rect.left; top = rect.top; right = rect.right; bottom = rect.bottom }
                  : CaptionDragPolicy.CaptionRect)
        else None

type private CaptionButtonSnapshot =
    { owner: obj
      className: string
      bounds: CaptionDragPolicy.CaptionRect
      buttons: CaptionDragPolicy.CaptionRect list
      dpi: uint32 }

type private CaptionButtonObservation =
    { owner: obj
      className: string
      geometry: (CaptionDragPolicy.CaptionRect * uint32) option }

// One background MTA, never the hook thread or its message queue. A stalled
// provider can stall discovery, but cannot stall input or create more workers.
// No UIA elements cross threads; the hook reads immutable numeric snapshots.
type CaptionButtonCache() =
    let entries = ConcurrentDictionary<IntPtr, CaptionButtonSnapshot>()
    let mutable stopped = 0
    let requested = ConcurrentDictionary<IntPtr, byte>()
    let changed = new AutoResetEvent(false)
    // Only the MTA worker owns observations, including failed attempts.
    let observations = Dictionary<IntPtr, CaptionButtonObservation>()
    let remove hwnd = entries.TryRemove(hwnd) |> ignore
    let signal () = try changed.Set() |> ignore with :? ObjectDisposedException -> ()
    let requestRefresh hwnd =
        if Volatile.Read(&stopped) = 0 && requested.TryAdd(hwnd, 0uy) then signal()

    let geometry hwnd =
        match CaptionButtonNative.bounds hwnd with
        | Some bounds ->
            let dpi = CaptionButtonNative.GetDpiForWindow(hwnd)
            if dpi <> 0u then Some(bounds, dpi) else None
        | None -> None

    let discover hwnd owner className bounds dpi =
        try
            // Invalidate before querying, including explicit refreshes.
            remove hwnd
#if DEBUG
            PerfTrace.count "captionButtons.query"
#endif
            let root = AutomationElement.FromHandle(hwnd)
            // Scope is this registered window, never the desktop.
            // Both custom and native accessibility providers expose
            // Button controls. No Name, title or localized label is read.
            let condition = PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)
            let request = CacheRequest()
            request.AutomationElementMode <- AutomationElementMode.None
            request.Add(AutomationElement.IsOffscreenProperty)
            request.Add(AutomationElement.BoundingRectangleProperty)
            use cacheScope = request.Activate()
            let elements = root.FindAll(TreeScope.Descendants, condition)
            let buttons =
                [ for index in 0 .. elements.Count - 1 do
                    let current = elements.[index].Cached
                    if not current.IsOffscreen then
                        let r = current.BoundingRectangle
                        if not r.IsEmpty &&
                           not (Double.IsNaN(r.Left) || Double.IsNaN(r.Top) ||
                                Double.IsNaN(r.Right) || Double.IsNaN(r.Bottom)) &&
                           r.Left >= float Int32.MinValue && r.Top >= float Int32.MinValue &&
                           r.Right <= float Int32.MaxValue && r.Bottom <= float Int32.MaxValue then
                            // Round inward: do not admit pixels outside
                            // the provider's rectangle on fractional DPI.
                            let button : CaptionDragPolicy.CaptionRect =
                                { left = int (Math.Ceiling r.Left); top = int (Math.Ceiling r.Top)
                                  right = int (Math.Floor r.Right); bottom = int (Math.Floor r.Bottom) }
                            if CaptionDragPolicy.validCaptionButton bounds button then yield button ]
            if Volatile.Read(&stopped) = 0 && CaptionDragTargets.ownedBy hwnd owner &&
               geometry hwnd = Some(bounds, dpi) then
                entries.[hwnd] <- { owner = owner; className = className; bounds = bounds
                                    // Bound hook work, prioritizing actual topmost button rectangles.
                                    buttons = buttons |> List.sortBy (fun r -> r.top) |> List.truncate 128
                                    dpi = dpi }
            else remove hwnd
        with _ -> remove hwnd

    let refresh () =
        for entry in entries.ToArray() do
            if not (CaptionDragTargets.ownedBy entry.Key entry.Value.owner) then remove entry.Key
        for hwnd in observations.Keys |> Seq.toArray do
            if not (CaptionDragTargets.contains hwnd) then observations.Remove(hwnd) |> ignore
        for target in CaptionDragTargets.snapshot() do
            if Volatile.Read(&stopped) = 0 then
                let hwnd, owner = target.Key, target.Value
                let hadRequest = requested.ContainsKey(hwnd)
                let mutable queried = false
                try
                    let previous =
                        match observations.TryGetValue(hwnd) with
                        | true, observation when obj.ReferenceEquals(observation.owner, owner) -> observation
                        | _ ->
                            remove hwnd
                            { owner = owner; className = Win32Helper.GetClassName(hwnd); geometry = None }
                    let candidate = CaptionDragPolicy.needsCaptionButtonAreas previous.className
                    if candidate then
                        let current = geometry hwnd
                        let shouldQuery = CaptionDragPolicy.captionButtonRefreshNeeded candidate
                                              hadRequest (current <> previous.geometry)
                        // Remember this attempt even if UIA fails. The slow tick
                        // checks only local geometry; it never retries unchanged failures.
                        observations.[hwnd] <- { previous with geometry = current }
                        match current with
                        | Some(bounds, dpi) when shouldQuery ->
                            queried <- true
                            discover hwnd owner previous.className bounds dpi
                        | None -> remove hwnd
                        | _ -> ()
                    else observations.[hwnd] <- previous
                with _ -> remove hwnd
                // Coalesce requests arriving during the query with this refresh.
                // A request arriving after an unchanged observation must
                // survive for the next pass instead of being silently lost.
                if hadRequest || queried then requested.TryRemove(hwnd) |> ignore
        for hwnd in requested.Keys do
            if not (CaptionDragTargets.contains hwnd) then requested.TryRemove(hwnd) |> ignore

    let run () =
        // UIA rectangles and low-level hook points are physical screen pixels.
        // GetWindowRect must use that same space on this worker too.
        let previous = CaptionButtonNative.SetThreadDpiAwarenessContext(IntPtr(-4))
        try
            if previous <> IntPtr.Zero then
                while Volatile.Read(&stopped) = 0 do
                    refresh()
                    // No UIA polling: wake for a coalesced miss, or once per
                    // second to check registration, bounds and DPI cheaply.
                    changed.WaitOne(1000) |> ignore
        finally
            entries.Clear()
            if previous <> IntPtr.Zero then
                CaptionButtonNative.SetThreadDpiAwarenessContext(previous) |> ignore

    let worker = Thread(ThreadStart(fun () ->
        try run() with _ -> entries.Clear()
        Volatile.Write(&stopped, 1)
        changed.Dispose()), IsBackground = true, Name = "Caption button discovery")
    do
        worker.SetApartmentState(ApartmentState.MTA)
        worker.Start()

    member this.TryButton(hwnd, bounds, x, y) =
        let candidate, valid, buttons, className =
            match entries.TryGetValue(hwnd) with
            | true, entry ->
                let valid = CaptionDragPolicy.captionButtonCacheValid
                                (CaptionDragTargets.ownedBy hwnd entry.owner) bounds entry.bounds
                                (CaptionButtonNative.GetDpiForWindow(hwnd)) entry.dpi
                true, valid, entry.buttons, entry.className
            | _ ->
                // Class lookup is local Win32 metadata, never a UIA call.
                let className = Win32Helper.GetClassName(hwnd)
                CaptionDragPolicy.needsCaptionButtonAreas className, false, [], className
        match CaptionDragPolicy.captionButtonDecision CaptionDragPolicy.hitCaption
                  candidate valid bounds buttons x y with
        | CaptionDragPolicy.PassButton -> Some className
        | CaptionDragPolicy.RefreshButtons ->
            if CaptionDragTargets.contains hwnd then requestRefresh hwnd
            None
        | CaptionDragPolicy.KeepCaption -> None

    interface IDisposable with
        member this.Dispose() =
            Volatile.Write(&stopped, 1)
            signal()
            entries.Clear()
            // A provider may be hung. Never wait for it on the input thread.
