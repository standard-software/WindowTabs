namespace Bemo

open System.Windows.Forms

type ManagerViewService() =
    // If a settings dialog is already open, just bring it to the front
    // instead of constructing a second DesktopManagerForm. The second
    // form would otherwise leak the named-mutex ownership (M1) by
    // overwriting DesktopManagerFormState.mutex with a non-owning M2,
    // leaving the dialog permanently un-openable after it closes.
    let activateExistingIfAny() =
        match DesktopManagerFormState.currentForm with
        | Some(existing) ->
            try
                if existing.WindowState = FormWindowState.Minimized then
                    existing.WindowState <- FormWindowState.Normal
                existing.Activate()
                existing.BringToFront()
            with _ -> ()
            true
        | None -> false

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
            if not (activateExistingIfAny()) then
                let form = new DesktopManagerForm()
                form.show()

        member x.show(view) =
            if not (activateExistingIfAny()) then
                let form = new DesktopManagerForm()
                form.showView(view)