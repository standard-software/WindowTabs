namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms


type InteractionView() =
    let mutable refreshing = false
    let refreshers = ResizeArray<unit -> unit>()
    let save key value =
        if not refreshing then Services.settings.setValue(key, value)

    let settingsProperty name =
        {
            new IProperty<'a> with
                member x.value
                    with get() = unbox<'a>(Services.settings.getValue(name))
                    and set(value) = save name (box(value))
        }
        

    // Next Tab / Previous Tab and the Ctrl+1 .. Ctrl+9 check box used to be
    // rows of this tab; they are on the Shortcut Keys tab now
    // (ShortcutKeysView), under the same settings keys.
    let table =
        
        let checkBox (prop:IProperty<bool>) = 
            let checkbox = BoolEditor() :> IPropEditor
            checkbox.value <- box(prop.value)
            checkbox.changed.Add <| fun() ->
                if not refreshing then prop.value <- unbox<bool>(checkbox.value)
            refreshers.Add(fun () -> checkbox.value <- box(prop.value))
            checkbox.control

        let settingsCheckbox key = checkBox(settingsProperty(key))
        
        let settingsRadios key normalize options =
            UIHelper.settingsRadios refreshers save key normalize options

        let buttonModeRadios key captions =
            List.zip [TabButtonPolicy.activeOnly; TabButtonPolicy.all; TabButtonPolicy.none] captions
            |> settingsRadios key TabButtonPolicy.normalize

        let snapChangeTabPositionRadios =
            settingsRadios "changeTabPositionOnSnap"
                (fun mode -> if mode = "nochange" then "nochange" else "change")
                ["change", "ChangeTabPositionOnSnapWhenUniform"; "nochange", "ChangeTabPositionOnSnapNever"]

        let lockPositionCheckbox = settingsCheckbox "lockWindowPosition"
        let gap = new Panel()
        let fields = List2([
            ("TabCloseButton", buttonModeRadios "tabCloseButton" ["TabCloseActiveOnly"; "TabCloseAll"; "TabCloseNone"])
            ("TabPinButton", buttonModeRadios "tabPinButton" ["TabPinActiveOnly"; "TabPinAll"; "TabPinNone"])
            ("EnableHoverActivate", settingsCheckbox "enableHoverActivate")
            ("LockWindowPosition", lockPositionCheckbox)
            ("", gap :> Control)
            ("ChangeTabPositionOnSnap", snapChangeTabPositionRadios)
            ("SnapTabHeightMargin", settingsCheckbox "snapTabHeightMargin")
            ("SnapOnDragDetach", settingsCheckbox "snapOnDragDetach")
        ])

        let formPanel = UIHelper.form fields
        // Radio groups already include their row spacing.
        for row in [0; 1; 5] do
            formPanel.GetControlFromPosition(1, row).Margin <- Padding(0)
        // An empty row avoids the caption's full-row minimum height.
        let gapRow = 4
        for column in 0 .. 1 do
            let control = formPanel.GetControlFromPosition(column, gapRow)
            formPanel.Controls.Remove(control)
            control.Dispose()
        formPanel.RowStyles.[gapRow].SizeType <- SizeType.Absolute
        formPanel.RowStyles.[gapRow].Height <- float32 (UIHelper.settingsRowHeightPx / 2)

        formPanel.Dock <- DockStyle.Fill
        formPanel.Padding <- UIHelper.settingsPagePadding
        formPanel

    member this.refresh() =
        let previous = refreshing
        refreshing <- true
        try
            for refresh in refreshers do refresh()
        finally
            refreshing <- previous

    interface ISettingsView with
        member x.key = SettingsViewType.InteractionSettings
        member x.title = Localization.getString("Interaction")
        member x.control = table :> Control


type GeneralView() =
    let mutable refreshing = false
    let refreshers = ResizeArray<unit -> unit>()
    let checkBox (prop: IProperty<bool>) =
        let editor = BoolEditor() :> IPropEditor
        editor.value <- box prop.value
        editor.changed.Add(fun () -> if not refreshing then prop.value <- unbox<bool> editor.value)
        refreshers.Add(fun () -> editor.value <- box prop.value)
        editor.control
    let settingsCheckbox key =
        checkBox {
            new IProperty<bool> with
                member _.value
                    with get() = unbox<bool>(Services.settings.getValue key)
                    and set value = Services.settings.setValue(key, box value) }
    let table =
        let fields = List2([
            ("RunAtStartup", settingsCheckbox "runAtStartup")
            ("IsTabbingEnabledForAllProcessesByDefault", checkBox(prop<IFilterService, bool>(Services.filter, "isTabbingEnabledForAllProcessesByDefault")))
            ("GroupTaskbarButtons", settingsCheckbox "groupTaskbarButtons")
            ("HideInactiveTabs", settingsCheckbox "hideInactiveTabs")
            ("HideTabsOnFullscreen", settingsCheckbox "hideTabsOnFullscreen")
            ("HideTabsWhileMoving", settingsCheckbox "hideTabsWhileMoving")
        ])
        let panel = UIHelper.form fields
        panel.Padding <- UIHelper.settingsPagePadding
        panel
    member _.refresh() =
        let previous = refreshing
        refreshing <- true
        try
            for refresh in refreshers do refresh()
        finally
            refreshing <- previous
    interface ISettingsView with
        member _.key = SettingsViewType.GeneralSettings
        member _.title = Localization.getString("General")
        member _.control = table :> Control
