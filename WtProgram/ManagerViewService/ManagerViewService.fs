namespace Bemo

open System.Windows.Forms

type ManagerViewService() =
    let presentationKey() =
        let root = Services.settings.root
        // Ordinary settings are reloaded into the existing controls before Show.
        // Language and dark mode affect captions and native control types.
        sprintf "%s|%b" Localization.currentLanguage (root.getBool("EnableDarkMode").def(false))

    let mutable cached: (string * DesktopManagerForm) option = None
    let mutable preparing = false
    let getForm() =
        let key = presentationKey()
        match cached with
        | Some(oldKey, form) when oldKey = key && not form.isDisposed -> form
        | old ->
            preparing <- true
            try
                cached <- None
                old |> Option.iter(fun (_, form) -> form.dispose())
                let form = DesktopManagerForm()
                try
                    form.preload()
                    cached <- Some(key, form)
                    form
                with _ ->
                    form.dispose()
                    reraise()
            finally
                preparing <- false

    do Application.ApplicationExit.Add(fun _ ->
        cached |> Option.iter(fun (_, form) -> form.dispose())
        cached <- None)

    let showSettings show =
        if not preparing && not Services.program.isDisabled then
            match DialogState.tryAcquire() with
            | None -> ()
            | Some session ->
                try
#if DEBUG
                    let timing = SettingsTiming.beginRun Services.program.version
#endif
                    let form = getForm()
#if DEBUG
                    timing.Mark("constructed")
#endif
                    show form session
#if DEBUG
                    timing.Mark("shown")
                    timing.Mark(sprintf "dpi=%.2f" (SettingsDpi.current()))
                    timing.Flush()
                    match DesktopManagerFormState.currentForm with
                    | Some visibleForm ->
                        if SettingsTiming.enabled() then visibleForm.Update()
                        timing.Mark("form-painted")
                        visibleForm.BeginInvoke(MethodInvoker(fun () ->
                            timing.Mark("ui-callback-after-show")
                            timing.Flush())) |> ignore
                    | None -> ()
#endif
                    if DesktopManagerFormState.currentForm.IsNone then session.Dispose()
                with _ ->
                    cached |> Option.iter(fun (_, form) -> form.dispose())
                    cached <- None
                    session.Dispose()
                    reraise()

    member this.preload() =
#if DEBUG
        let clock = System.Diagnostics.Stopwatch.StartNew()
        use process = System.Diagnostics.Process.GetCurrentProcess()
        let initialBytes = process.PrivateMemorySize64
        let initialHandles = process.HandleCount
#endif
        try
            getForm() |> ignore
            DesktopManagerFormState.log "settings preloaded hidden"
#if DEBUG
            process.Refresh()
            DesktopManagerFormState.log (sprintf "preload ms=%.1f privateBytesDelta=%d handlesDelta=%d gateBusy=%b"
                clock.Elapsed.TotalMilliseconds (process.PrivateMemorySize64 - initialBytes)
                (process.HandleCount - initialHandles) (DialogState.isBusy()))
#endif
        with ex ->
            DesktopManagerFormState.log (sprintf "settings preload failed: %O" ex)

    // The settings dialog used to be built inside Dpi.withUnawareContext,
    // which made Windows lay it out in 96-dpi units and bitmap-stretch the
    // whole window on a scaled monitor. Sizes stayed consistent that way, at
    // the price of the blur this version removes.
    //
    // It is now built on the process's ordinary Per-Monitor-V2 thread and
    // scaled explicitly by SettingsDpi (Shared/Dpi.fs), so it renders on the
    // monitor's own pixel grid. The two failures the old comment warned about
    // - clipped labels, overlapping columns - come from scaling the FONT
    // without scaling the layout; SettingsDpi scales both by the same factor,
    // plus the metrics WinForms' own scaling walk cannot reach (the TreeViewAdv
    // font and geometry, ToolStrip image sizes, owner-drawn item heights).
    //
    // The one thing that must NOT move to the aware coordinate space is the
    // workspace save / restore, whose rectangles are persisted to settings.json
    // and have to stay comparable with what earlier versions wrote. That is now
    // a two-call unaware island inside WorkspaceModel rather than a
    // dialog-wide one - see WorkspaceModel.createWorkspace / restoreWorkspace.
    interface IManagerView with
        member x.show() =
            DesktopManagerFormState.log("manager show requested")
            showSettings (fun form session -> form.show(session))

        member x.show(view) =
            showSettings (fun form session -> form.showView(view, session))

        // User close hides the cached form and releases its visible session.
        member x.close() =
            match DesktopManagerFormState.currentForm with
            | Some form -> (try form.Close() with _ -> ())
            | None -> ()
