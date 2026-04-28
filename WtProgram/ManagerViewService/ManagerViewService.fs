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

    interface IManagerView with
        member x.show() =
            if not (activateExistingIfAny()) then
                let form = new DesktopManagerForm()
                form.show()

        member x.show(view) =
            if not (activateExistingIfAny()) then
                let form = new DesktopManagerForm()
                form.showView(view)