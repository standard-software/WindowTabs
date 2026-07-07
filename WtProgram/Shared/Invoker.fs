namespace Bemo
open System
open System.Threading
open System.Windows.Forms

// Global flag to indicate force exit is in progress (used by watchdog)
module ForceExitState =
    let mutable isForceExiting = false

// Records, per thread, the synchronous cross-thread calls it is currently
// blocked in. When the watchdog detects a UI freeze it dumps this table to a
// log file, so a deadlock cycle can be identified from the log (thread A
// waiting for thread B, thread B waiting for thread A) instead of guessed at.
module SyncCallTracker =
    let private sync = obj()
    let private pending = System.Collections.Generic.Dictionary<int, (string * DateTime) list>()

    let enter (desc: string) =
        let tid = Thread.CurrentThread.ManagedThreadId
        lock sync <| fun() ->
            let cur = match pending.TryGetValue(tid) with | true, l -> l | _ -> []
            pending.[tid] <- (desc, DateTime.Now) :: cur

    let exit() =
        let tid = Thread.CurrentThread.ManagedThreadId
        lock sync <| fun() ->
            match pending.TryGetValue(tid) with
            | true, _ :: rest ->
                if rest.IsEmpty then pending.Remove(tid).ignore
                else pending.[tid] <- rest
            | _ -> ()

    let dump() =
        lock sync <| fun() ->
            pending
            |> Seq.collect (fun kv ->
                kv.Value |> List.map (fun (desc, at) ->
                    sprintf "thread %d: %s (since %s)" kv.Key desc (at.ToString("HH:mm:ss.fff"))))
            |> String.concat Environment.NewLine

type InvokeDelegate<'a> = delegate of unit -> 'a
[<AllowNullLiteral>]
type Invoker() as this =
    let ownerThreadId = Thread.CurrentThread.ManagedThreadId
    let form =
        let f = new Form()
        f.Handle.ignore
        f

    let lockDispose f = lock this <| fun() -> if form.IsDisposed.not && form.IsHandleCreated then f()

    do
        printfn "Invoker for %d" (System.Threading.Thread.CurrentThread.ManagedThreadId)

    member this.invokeRequired = form.InvokeRequired

    member this.invoke f=
        if this.invokeRequired then
            SyncCallTracker.enter (sprintf "Invoker.invoke, blocked waiting for thread %d" ownerThreadId)
            try
                form.Invoke(InvokeDelegate(fun() -> f()), null) :?> 'a
            finally
                SyncCallTracker.exit()
        else
            f()

    member this.asyncInvoke f =
        lockDispose <| fun() ->
            form.BeginInvoke(MethodInvoker(fun() -> f())).ignore

    interface IDisposable with
        member this.Dispose() =
            lockDispose <| fun() ->
                form.Dispose()

type InvokerService =
    [<DefaultValue>]
    [<ThreadStatic>]
    static val mutable private _invoker : Invoker

    static member invoker
        with get() =
            if InvokerService._invoker = null then
                InvokerService._invoker <- Invoker()
            InvokerService._invoker

// Deadlock guard for secondary UI threads (one per tab group).
//
// On a display-settings change (monitor added/removed, resolution change),
// Microsoft.Win32.SystemEvents — whose broadcast window lives on the MAIN
// thread — delivers DisplaySettingsChanged / UserPreferenceChanged to every
// WinForms control that subscribed from a group thread via that thread's
// SynchronizationContext.Send, i.e. the main thread BLOCKS until the group
// thread has processed the callback. At the same moment the group thread is
// typically handling the same broadcast on its own windows and makes a
// synchronous service call into the main thread (ServiceProxy /
// Invoker.invoke). Both threads then wait on each other forever — this is
// the freeze that the watchdog used to "fix" by force-restarting the app.
//
// Installing this context on a group thread before any control is created
// makes WinForms and SystemEvents capture it (WinForms only auto-installs
// its own context over a null/default one, never over a custom type).
// A cross-thread Send is then degraded to Post: the callback still runs on
// the correct (owning) thread, just asynchronously, so the sender never
// blocks and the cycle cannot form. Same-thread Send runs inline as before.
type NonBlockingSyncContext private (inner: SynchronizationContext, ownerThreadId: int) =
    inherit SynchronizationContext()
    new (inner: SynchronizationContext) =
        NonBlockingSyncContext(inner, Thread.CurrentThread.ManagedThreadId)
    override this.Post(d, state) = inner.Post(d, state)
    override this.Send(d, state) =
        if Thread.CurrentThread.ManagedThreadId = ownerThreadId
        then d.Invoke(state)
        else inner.Post(d, state)
    override this.CreateCopy() =
        NonBlockingSyncContext(inner, ownerThreadId) :> SynchronizationContext

module ThreadHelper =
    let queueBackground f =
        System.Threading.ThreadPool.QueueUserWorkItem(Threading.WaitCallback(fun _ -> f())).ignore

    let startOnThreadAndWait fStart =
        let evt = ManualResetEvent(false)
        let results = ref None
        let start() =
            // Must run before the first control is created on this thread so
            // that every SystemEvents subscription captures the non-blocking
            // context (see NonBlockingSyncContext above).
            let inner = new WindowsFormsSynchronizationContext()
            SynchronizationContext.SetSynchronizationContext(NonBlockingSyncContext(inner))
            results := Some(fStart())
            evt.Set().ignore
            Application.EnableVisualStyles()
            Application.Run()
        let thread = Thread(ThreadStart(start))
        thread.SetApartmentState(ApartmentState.STA)
        thread.Start()
        evt.WaitOne().ignore
        results.Value.Value

    let cancelablePostBack interval f =
        let timer = new System.Windows.Forms.Timer()
        timer.Interval <- interval
        timer.Tick.Add <| fun _ ->
            f()
            timer.Dispose()
        timer.Start()
        timer :> IDisposable