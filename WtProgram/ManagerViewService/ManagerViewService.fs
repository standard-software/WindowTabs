namespace Bemo

open System.Windows.Forms

type ManagerViewService() =
    let showSettings show =
        if not Services.program.isDisabled then
            match DialogState.tryAcquire() with
            | None -> ()
            | Some session ->
                try
                    let form = new DesktopManagerForm(session)
                    show form
                    if DesktopManagerFormState.currentForm.IsNone then session.Dispose()
                with _ ->
                    session.Dispose()
                    reraise()

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
            showSettings (fun form -> form.show())

        member x.show(view) =
            showSettings (fun form -> form.showView(view))

        // Through the form itself: its FormClosed handler releases the named
        // mutex and clears DesktopManagerFormState.currentForm, and nothing
        // else must touch either. (An earlier caller opened a second handle
        // to the mutex and released it before closing, which decremented the
        // dialog's own ownership before FormClosed ran and left the dialog
        // un-openable afterwards.)
        member x.close() =
            match DesktopManagerFormState.currentForm with
            | Some form -> (try form.Close() with _ -> ())
            | None -> ()
