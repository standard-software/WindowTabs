namespace Bemo
open System
open System.Drawing
open System.Windows.Forms

// The one way WindowTabs puts a message in front of the user.
//
// The system MessageBox came up BEHIND the settings dialog, which is always
// on top, and still took the focus: the dialog could not be used and the
// message could not be seen. This form follows the dark-mode setting, sits
// on top, is laid out for the monitor it opens on, copies itself with
// Ctrl+C in the system box's layout, and - when opened from the tray menu or a
// tab strip - closes the settings dialog first, as the "language changed"
// confirmation always has.
module AppDialog =
    type Buttons =
        | OkOnly
        | OkCancel

    type DefaultButton =
        | DefaultOk
        | DefaultCancel

    let private darkOn() =
        try
            match Services.settings.root.getBool("EnableDarkMode") with
            | Some(v) -> v
            | None -> false
        with _ -> false

    let private build (owner: IWin32Window option) (title: string) (message: string)
                      (buttons: Buttons) (defaultButton: DefaultButton) =
        let form = new Form()
        form.Text <- title
        form.FormBorderStyle <- FormBorderStyle.FixedDialog
        form.MaximizeBox <- false
        form.MinimizeBox <- false
        form.StartPosition <- (if owner.IsSome then FormStartPosition.CenterParent else FormStartPosition.CenterScreen)
        form.TopMost <- true
        form.ShowInTaskbar <- false
        form.KeyPreview <- true

        let label = new Label()
        label.Text <- message
        label.Location <- Point(30, 30)
        label.AutoSize <- true
        form.Controls.Add(label)

        let okBtn = new Button()
        okBtn.Text <- Localization.getString("OK")
        okBtn.DialogResult <- DialogResult.OK
        okBtn.Size <- Size(80, 30)
        form.Controls.Add(okBtn)

        let cancelBtn =
            match buttons with
            | OkCancel ->
                let b = new Button()
                b.Text <- Localization.getString("Cancel")
                b.DialogResult <- DialogResult.Cancel
                b.Size <- Size(80, 30)
                form.Controls.Add(b)
                Some(b)
            | OkOnly -> None

        // Enter takes the default button, Escape the cancelling one (OK when
        // there is only OK, so Escape still closes the box).
        let defaultBtn =
            match defaultButton, cancelBtn with
            | DefaultCancel, Some(c) -> c
            | _ -> okBtn
        form.AcceptButton <- defaultBtn
        form.CancelButton <- (match cancelBtn with Some(c) -> c | None -> okBtn)

        // Ctrl+C copies the box in the system message box's own layout -
        // title, message and button captions between rules of 27 dashes,
        // each caption followed by three spaces, CRLF line ends - so what
        // lands in a bug report looks like what people are used to.
        let clipboardText() =
            let rule = String('-', 27)
            let captions =
                okBtn.Text :: (match cancelBtn with Some(c) -> [c.Text] | None -> [])
                |> List.map (fun c -> c + "   ")
                |> String.concat ""
            String.Join("\r\n", [| rule; title; rule; message; rule; captions; rule; "" |])
        form.KeyDown.Add(fun e ->
            if e.Control && e.KeyCode = Keys.C then
                (try Clipboard.SetText(clipboardText()) with _ -> ())
                e.Handled <- true
                e.SuppressKeyPress <- true)

        // Owned by the settings dialog: laid out at its scale, since the box
        // opens centred on it. Stand-alone: the settings dialog is closed by
        // then, so the monitor is taken from the pointer. The Load handler
        // below runs afterwards and reads the scale this establishes.
        match owner with
        | Some(_) -> SettingsDpi.applyToChildDialog form
        | None -> SettingsDpi.applyAtCursor form

        form.Load.Add(fun _ ->
            // Size the form around the label so multi-byte strings (Japanese,
            // Chinese) fit comfortably, and the buttons under it.
            let margin = SettingsDpi.px 30
            let gap = SettingsDpi.px 10
            let buttonsWidth =
                okBtn.Width + (match cancelBtn with Some(c) -> gap + c.Width | None -> 0)
            let cw = max (max (label.Right + margin) (SettingsDpi.px 360)) (buttonsWidth + 2 * margin)
            let ch = label.Bottom + margin + okBtn.Height + margin
            form.ClientSize <- Size(cw, ch)
            let left = (cw - buttonsWidth) / 2
            let top = label.Bottom + margin
            okBtn.Location <- Point(left, top)
            cancelBtn |> Option.iter (fun c -> c.Location <- Point(left + okBtn.Width + gap, top))
            defaultBtn.Select())

        if darkOn() then
            DarkMode.applyDarkColorsBeforeShow form
            form.HandleCreated.Add(fun _ ->
                try DarkMode.applyDarkThemeBranch15ToForm form true
                with _ -> ())
        form

    let private run (owner: IWin32Window option) title message buttons defaultButton : DialogResult =
        use form = build owner title message buttons defaultButton
        match owner with
        | Some(o) -> form.ShowDialog(o)
        | None -> form.ShowDialog()

    /// A message from the tray menu or a tab strip. The settings dialog is
    /// closed first: it is always on top, and a box behind it would take the
    /// focus from a window the user could then not use.
    let show (title: string) (message: string) (buttons: Buttons) (defaultButton: DefaultButton) : DialogResult =
        (try Services.managerView.close() with _ -> ())
        run None title message buttons defaultButton

    /// A message with only OK, from the tray menu or a tab strip.
    let info (title: string) (message: string) =
        show title message OkOnly DefaultOk |> ignore

    /// A question with OK and Cancel, from the tray menu or a tab strip.
    /// Cancel is the default, so an accidental Enter does nothing.
    let confirm (title: string) (message: string) : bool =
        show title message OkCancel DefaultCancel = DialogResult.OK

    /// A message from INSIDE the settings dialog: owned by it and centred on
    /// it, so it comes up in front and the dialog stays open behind it.
    let showOwned (owner: IWin32Window) (title: string) (message: string) =
        run (Some owner) title message OkOnly DefaultOk |> ignore
