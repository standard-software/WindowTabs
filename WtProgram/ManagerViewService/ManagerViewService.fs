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

    // The settings dialog is legacy WinForms: no AutoScaleMode is set anywhere,
    // control sizes are hard-coded 96-dpi pixels, and the tree / tab / hotkey
    // controls were all laid out for a DPI-unaware process. Now that the
    // process is Per-Monitor-V2 aware, building it on an aware thread would
    // draw its text at the monitor DPI inside 96-dpi sized controls - clipped
    // labels and overlapping columns.
    //
    // Building it under a DPI-unaware thread context keeps that window unaware
    // for its whole lifetime, so Windows scales it exactly as it did before
    // this change: identical appearance, and identical (virtualized)
    // coordinates for the workspace save/restore that lives inside it, so
    // workspaces saved by earlier versions still restore correctly. Child
    // dialogs opened from the form inherit the context, because Windows
    // restores a window's own awareness while dispatching its messages.
    //
    // This is a deliberate, scoped decision: the settings dialog stays as
    // blurry (or as sharp) on a scaled monitor as it has always been. Making
    // it DPI-native is a separate piece of work on a legacy layout, and doing
    // it here would risk the regression above for no gain on the tab strips,
    // which are what this change is about.
    let showUnaware f = Dpi.withUnawareContext f

    interface IManagerView with
        member x.show() =
            if not (activateExistingIfAny()) then
                showUnaware <| fun() ->
                    let form = new DesktopManagerForm()
                    form.show()

        member x.show(view) =
            if not (activateExistingIfAny()) then
                showUnaware <| fun() ->
                    let form = new DesktopManagerForm()
                    form.showView(view)