namespace Bemo
open System
open System.Drawing
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms

// The "Shortcut Keys" tab of the settings dialog.
//
// One form in the layout the other tabs use (UIHelper.form: 250-px caption
// column, input column). Rows, top to bottom: "Activate Tab" with the 3 x 3
// grid of number fields and the two mode check boxes under it; Next Tab;
// Previous Tab; "Add a new tab to the right of the active tab". Every field
// is a HotKeyEditor. Next / Previous moved here from the Behavior tab and
// keep their settings keys, so nobody's setting is lost.
//
// The number fields are what HotKeyPolicy.numberKeyMode says they are. In
// the Ctrl or Alt mode they are locked (disabled and read-only) and show
// Ctrl + N / Alt + N; the user's own keys are neither shown nor touched. In
// the custom mode they show and edit the user's keys. A value this code puts
// into a field is never written back - `suppress` guards the changed
// handlers - so switching Ctrl on and off again leaves the custom keys
// exactly as they were.
//
// Nothing here registers a hot key. Each write goes through
// Services.program.setHotKey or Services.settings.setValue, and Program
// resyncs from there (Program.syncHotKeys).
type ShortcutKeysView() =
    // Set while this code changes a control's value itself, so the handlers
    // that write to the settings stay quiet.
    let mutable suppress = false

    let hotKeyEditor () =
        let editor = HotKeyEditor() :> IPropEditor
        editor.control.Margin <- Padding(0, 5, 0, 5)
        editor

    // A hot key field that reads and writes one settings key. The value is
    // put in before the handler is attached, so opening the tab writes
    // nothing.
    let boundEditor key =
        let editor = hotKeyEditor()
        editor.value <- box(Services.program.getHotKey(key))
        editor.changed.Add <| fun () ->
            if not suppress then
                Services.program.setHotKey key (unbox<int>(editor.value))
        editor

    // ----------------------------------------------------- activate tab --

    let numberEditors = HotKeyPolicy.numbers |> List.map (fun n -> n, hotKeyEditor())

    // The check boxes carry their own caption, "[x] Enable Ctrl+1, ...",
    // inside the item's input column, rather than the Behavior tab's
    // "caption in the label column, bare check box in the input column".
    // They are sub-options of the "Activate Tab" item - the user asked for
    // them as rows under its 3 x 3 grid - and a caption in the label column
    // would read as a separate item of the form. The captions are also the
    // longest on the tab and would wrap in the 250-px column.
    let modeCheckBox captionKey =
        let checkBox = CheckBox()
        checkBox.Text <- Localization.getString captionKey
        checkBox.AutoSize <- true
        checkBox
    let ctrlCheck = modeCheckBox "EnableCtrlNumberHotKey"
    let altCheck = modeCheckBox "EnableAltNumberHotKey"

    let storedNumberKey n = Services.program.getHotKey(HotKeyPolicy.activateTabKey n)

    let currentMode () = HotKeyPolicy.numberKeyMode ctrlCheck.Checked altCheck.Checked

    // Put the nine fields in the state the check boxes ask for.
    let applyMode () =
        let mode = currentMode()
        let editable = HotKeyPolicy.numberFieldsEditable mode
        let previous = suppress
        suppress <- true
        try
            for (n, editor) in numberEditors do
                editor.value <- box(HotKeyPolicy.numberKeyCode mode storedNumberKey n)
                editor.control.Enabled <- editable
                // Disabled AND read-only, as asked. The managed (dark-mode)
                // control is read-only by construction - it records keys
                // itself and must never accept typed text - so it stays
                // read-only in the custom mode too.
                match editor.control with
                | :? TextBox as textBox -> textBox.ReadOnly <- (not editable) || HotKeyControl.UseManaged
                | _ -> ()
        finally
            suppress <- previous

    // Write the check boxes to the settings - only the flag that differs
    // from what is stored, since each write is a file save and a resync of
    // the hot keys. A file with both flags on (hand-edited; shown as Ctrl
    // alone, see HotKeyPolicy.numberKeyMode) gets both written the first
    // time the user touches a box, which is what makes the file agree with
    // the dialog from then on.
    let saveMode () =
        for (checkBox, setting) in [ ctrlCheck, HotKeyPolicy.enableCtrlNumberSetting
                                     altCheck, HotKeyPolicy.enableAltNumberSetting ] do
            let stored = unbox<bool>(Services.settings.getValue(setting))
            if stored <> checkBox.Checked then
                Services.settings.setValue(setting, box(checkBox.Checked))

    // The two check boxes are exclusive: switching one on switches the other
    // off. Both off is allowed - that is the custom mode.
    let onModeCheckChanged (changed: CheckBox) (other: CheckBox) =
        if not suppress then
            if changed.Checked && other.Checked then
                suppress <- true
                try other.Checked <- false
                finally suppress <- false
            saveMode()
            applyMode()

    let activateTabPanel =
        // Same shape as the Appearance tab's colour grid: fixed columns, the
        // fields sharing the width. Rows:
        //   [1][field]  [2][field]  [3][field]
        //   [4][field]  [5][field]  [6][field]
        //   [7][field]  [8][field]  [9][field]
        //   [x] Enable Ctrl+1, ...Ctrl+9 to activate tab
        //   [x] Enable Alt+1, ...Alt+9 to activate tab
        let table = TableLayoutPanel()
        // AutoSize on a nested container is mandatory here for the reason
        // AppearanceView's pinned-width panel gives: an AutoSize row takes a
        // non-AutoSize child's CURRENT size, and a never-laid-out panel is
        // still at its 200 x 100 default.
        table.AutoSize <- true
        table.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        table.Margin <- Padding(0)
        table.ColumnCount <- 6
        table.RowCount <- 5
        for _ in 1 .. 3 do
            table.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore          // number
            table.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 33.33f)) |> ignore   // field
        for _ in 1 .. 3 do
            table.RowStyles.Add(RowStyle(SizeType.Absolute, 35.0f)) |> ignore
        // Five pixels of section space above Ctrl and below Alt. The margins
        // and row heights grow together so neither caption is clipped.
        table.RowStyles.Add(RowStyle(SizeType.Absolute, 40.0f)) |> ignore
        table.RowStyles.Add(RowStyle(SizeType.Absolute, 40.0f)) |> ignore
        for (n, editor) in numberEditors do
            let row = (n - 1) / 3
            let column = ((n - 1) % 3) * 2
            let label = UIHelper.label (string n)
            label.Anchor <- AnchorStyles.Left
            label.Margin <- Padding(0, 5, 6, 5)
            editor.control.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
            editor.control.Margin <- Padding(0, 5, 12, 5)
            table.Controls.Add(label, column, row)
            table.Controls.Add(editor.control, column + 1, row)
        table.Controls.Add(ctrlCheck, 0, 3)
        table.SetColumnSpan(ctrlCheck, 6)
        ctrlCheck.Margin <- Padding(0, 10, 0, 5)
        table.Controls.Add(altCheck, 0, 4)
        table.SetColumnSpan(altCheck, 6)
        altCheck.Margin <- Padding(0, 5, 0, 10)
        table

    do
        // Initial state from the file. A file with both flags on shows Ctrl
        // alone, which is what HotKeyPolicy makes of it too; nothing is
        // written until the user changes something.
        let ctrl = unbox<bool>(Services.settings.getValue(HotKeyPolicy.enableCtrlNumberSetting))
        let alt = unbox<bool>(Services.settings.getValue(HotKeyPolicy.enableAltNumberSetting))
        suppress <- true
        ctrlCheck.Checked <- ctrl
        altCheck.Checked <- alt && not ctrl
        suppress <- false
        for (n, editor) in numberEditors do
            editor.changed.Add <| fun () ->
                if not suppress && HotKeyPolicy.numberFieldsEditable (currentMode()) then
                    Services.program.setHotKey (HotKeyPolicy.activateTabKey n) (unbox<int>(editor.value))
        ctrlCheck.CheckedChanged.Add <| fun _ -> onModeCheckChanged ctrlCheck altCheck
        altCheck.CheckedChanged.Add <| fun _ -> onModeCheckChanged altCheck ctrlCheck
        applyMode()

    // --------------------------------------------------------- the form --

    let nextTabEditor = boundEditor HotKeyPolicy.nextTabKey
    let prevTabEditor = boundEditor HotKeyPolicy.prevTabKey
    let newTabRightEditor = boundEditor HotKeyPolicy.newTabRightKey

    let panel =
        let form = UIHelper.form (List2([
            ("ActivateTab", activateTabPanel :> Control)
            ("NextTab", nextTabEditor.control)
            ("PrevTab", prevTabEditor.control)
            ("NewTabRightOfActive", newTabRightEditor.control)
            ]))
        // The nested table itself owns five complete 35-px rows. An additional
        // outer margin would make the following shortcut start off-grid.
        activateTabPanel.Margin <- Padding(0)
        form.Dock <- DockStyle.Fill
        // Same padding as the Appearance and Behavior tabs.
        form.Padding <- Padding(10)
        form

    member this.refresh() =
        let previous = suppress
        suppress <- true
        try
            let ctrl = unbox<bool>(Services.settings.getValue(HotKeyPolicy.enableCtrlNumberSetting))
            let alt = unbox<bool>(Services.settings.getValue(HotKeyPolicy.enableAltNumberSetting))
            ctrlCheck.Checked <- ctrl
            altCheck.Checked <- alt && not ctrl
            for key, editor in [HotKeyPolicy.nextTabKey, nextTabEditor;
                                HotKeyPolicy.prevTabKey, prevTabEditor;
                                HotKeyPolicy.newTabRightKey, newTabRightEditor] do
                editor.value <- box(Services.program.getHotKey(key))
            applyMode()
        finally
            suppress <- previous

    interface ISettingsView with
        member x.key = SettingsViewType.ShortcutKeySettings
        member x.title = Localization.getString("ShortcutKeys")
        member x.control = panel :> Control
