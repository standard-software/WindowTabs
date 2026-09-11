// Run with .NET Framework fsi.exe after a normal Debug build; see SettingsTests.md.
// This test uses in-memory services, never displays windows, and never saves files.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../Newtonsoft.Json.dll"
#r "../WtProgram/bin/Debug/FSharp.PowerPack.dll"
#r "../WtProgram/bin/Debug/WindowTabs.exe"
#r "../WtProgram/bin/Debug/Win32.dll"
open System
open System.Collections.Generic
open System.Windows.Forms
open System.Runtime.Remoting.Proxies
open System.Runtime.Remoting.Messaging
open Microsoft.FSharp.Reflection
open Bemo
open Newtonsoft.Json.Linq

let check name condition =
    if not condition then failwith name
    printfn "PASS %s" name

let values = Dictionary<string,obj>()
let keys = Dictionary<string,int>()
let mutable writes = 0
let mutable root = JObject()
// Build a fixture without constructing the real file-backed Settings service.
let defaults =
    let fields = FSharpType.GetRecordFields(typeof<TabAppearanceInfo>) |> Array.map(fun field ->
        if field.Name = "tabPinnedTabWidth" then box 90
        elif field.PropertyType = typeof<int> then box 25
        elif field.PropertyType = typeof<bool> then box true
        else box Drawing.Color.Black)
    FSharpValue.MakeRecord(typeof<TabAppearanceInfo>, fields) :?> TabAppearanceInfo
let mutable appearance = defaults
for name in ["runAtStartup"; "hideInactiveTabs"; "enableHoverActivate";
             "hideTabsOnFullscreen"; "hideTabsWhileMoving"; "snapTabHeightMargin";
             "enableCtrlNumberHotKey"; "enableAltNumberHotKey"] do values.[name] <- box false
for name, value in ["tabPositionByDefault", "TopLeft"; "hideTabsWhenDownByDefault", "never";
                    "changeTabPositionOnSnap", "change"; "tabVerticalDirection", "auto"] do
    values.[name] <- box value
values.["hideTabsDelayMilliseconds"] <- box 3000
let settings =
    { new ISettings with
        member _.getValue key = if key = "tabAppearance" then box appearance else values.[key]
        member _.setValue((key,value)) = writes <- writes + 1; values.[key] <- value
        member _.notifyValue _ _ = ()
        member _.root with get() = root and set value = writes <- writes + 1; root <- value }
Services.register(settings, false)

type Stub(t: Type, invoke: IMethodCallMessage -> obj) =
    inherit RealProxy(t)
    override _.Invoke(message) =
        let call = message :?> IMethodCallMessage
        try ReturnMessage(invoke call, null, 0, call.LogicalCallContext, call) :> IMessage
        with ex -> ReturnMessage(ex, call) :> IMessage
let program = Stub(typeof<IProgram>, fun call ->
    match call.MethodName with
    | "get_tabAppearanceInfo" -> box appearance
    | "getHotKey" -> let key = unbox<string> call.Args.[0] in box(if keys.ContainsKey key then keys.[key] else 0)
    | "setHotKey" -> writes <- writes + 1; null
    | name when name.EndsWith("TabAppearanceInfo") -> box defaults
    | name -> failwithf "Unexpected program call: %s" name)
Services.register(program.GetTransparentProxy() :?> IProgram, false)
let mutable tabbing = false
let filter = Stub(typeof<IFilterService>, fun call ->
    match call.MethodName with
    | "get_isTabbingEnabledForAllProcessesByDefault" -> box tabbing
    | "set_isTabbingEnabledForAllProcessesByDefault" -> writes <- writes + 1; null
    | name -> failwithf "Unexpected filter call: %s" name)
Services.register(filter.GetTransparentProxy() :?> IFilterService, false)
let rec controls (control: Control) = seq {
    yield control
    for child in control.Controls do yield! controls child }
let appearanceView = AppearanceView()
let behavior = HotKeyView()
let shortcuts = ShortcutKeysView()
let views = [appearanceView :> ISettingsView; behavior :> ISettingsView; shortcuts :> ISettingsView]
let host = new Form()
for view in views do host.Controls.Add(view.control)
for control in controls host do control.Handle |> ignore
let originalControls = views |> List.collect(fun v -> controls v.control |> Seq.toList)
let refresh() = appearanceView.refresh(); behavior.refresh(); shortcuts.refresh()
let baselineWrites = writes
values.["runAtStartup"] <- box true
values.["hideTabsWhenDownByDefault"] <- box "down"
values.["hideTabsDelayMilliseconds"] <- box 1234
values.["tabPositionByDefault"] <- box "TopRight"
values.["changeTabPositionOnSnap"] <- box "nochange"
values.["tabVerticalDirection"] <- box TabBehaviorPolicy.verticalAlwaysDown
values.["enableCtrlNumberHotKey"] <- box true
values.["enableAltNumberHotKey"] <- box true
tabbing <- true
appearance <- { defaults with tabHeight = 31; tabPinnedTabWidth = 110; tabInactiveTabColor = Drawing.Color.Red }
keys.[HotKeyPolicy.nextTabKey] <- 0x0271
refresh()
check "refresh never saves settings or re-registers hotkeys" (writes = baselineWrites)
check "refresh preserves every control instance" (originalControls = (views |> List.collect(fun v -> controls v.control |> Seq.toList)))
let behaviorControls = controls (behavior :> ISettingsView).control |> Seq.toList
let combos = behaviorControls |> List.choose(function :? ComboBox as c -> Some c | _ -> None)
check "all behavior comboboxes reload external values" (combos.Length = 3 && combos |> List.forall(fun c -> c.SelectedIndex = 1))
check "delay reloads its value and enabled state" (behaviorControls |> List.exists(function :? TextBox as t -> t.Text = "1234" && t.Enabled | _ -> false))
let checks = behaviorControls |> List.choose(function :? CheckBox as c -> Some c | _ -> None)
check "behavior checkboxes reload settings and filter service" ((checks |> List.filter(fun c -> c.Checked)).Length = 2)
let shortcutControls = controls (shortcuts :> ISettingsView).control |> Seq.toList
let modeChecks = shortcutControls |> List.choose(function :? CheckBox as c -> Some c | _ -> None)
check "conflicting number-key flags display Ctrl only without saving" (modeChecks.[0].Checked && not modeChecks.[1].Checked && unbox<bool> values.["enableAltNumberHotKey"])
let numeric = controls (appearanceView :> ISettingsView).control |> Seq.choose(function :? NumericUpDown as n -> Some n | _ -> None) |> Seq.toList
check "appearance numeric values reload" (numeric |> List.exists(fun n -> n.Value = 31M))
let timer = Diagnostics.Stopwatch.StartNew()
for _ in 1..20 do refresh()
printfn "Hidden-control refresh average: %.2f ms (not full dialog show time)" (timer.Elapsed.TotalMilliseconds / 20.0)
check "repeated refresh remains read-only" (writes = baselineWrites)
values.["hideTabsWhenDownByDefault"] <- box "never"
values.["enableCtrlNumberHotKey"] <- box false
values.["enableAltNumberHotKey"] <- box false
refresh()
check "dependent controls return to disabled/custom states" (not (behaviorControls |> List.pick(function :? TextBox as t -> Some t | _ -> None)).Enabled && modeChecks |> List.forall(fun c -> not c.Checked))
check "return refresh remains read-only" (writes = baselineWrites)
// Check every Behavior row, not just one representative checkbox.
let behaviorTable = (behavior :> ISettingsView).control :?> TableLayoutPanel
check "Behavior contains the audited eleven rows" (behaviorTable.RowCount = 11)
let boolRows = [0, "runAtStartup"; 1, "hideInactiveTabs"; 2, "filter";
                3, "enableHoverActivate"; 8, "hideTabsOnFullscreen";
                9, "hideTabsWhileMoving"; 10, "snapTabHeightMargin"]
for row, key in boolRows do
    let checkbox = behaviorTable.GetControlFromPosition(1, row) :?> CheckBox
    for expected in [false; true; false] do
        if key = "filter" then tabbing <- expected else values.[key] <- box expected
        behavior.refresh()
        check (sprintf "Behavior row %d %s=%b" row key expected) (checkbox.Checked = expected)
for row, key, options in [
    4, "tabPositionByDefault", ["TopLeft"; "TopRight"]
    5, "changeTabPositionOnSnap", ["change"; "nochange"]
    6, "tabVerticalDirection", [TabBehaviorPolicy.verticalAuto; TabBehaviorPolicy.verticalAlwaysDown]] do
    let combo = behaviorTable.GetControlFromPosition(1,row) :?> ComboBox
    for index, value in options |> List.indexed do
        values.[key] <- box value
        behavior.refresh()
        check (sprintf "Behavior row %d %s=%s" row key value) (combo.SelectedIndex = index)
let radioControls = controls (behaviorTable.GetControlFromPosition(1,7)) |> Seq.choose(function :? RadioButton as radio -> Some radio | _ -> None) |> Seq.toArray
for index, mode in ["never"; "down"; "doubleclick"] |> List.indexed do
    values.["hideTabsWhenDownByDefault"] <- box mode
    behavior.refresh()
    check ("Behavior row 7 mode=" + mode) (radioControls |> Array.mapi(fun i r -> r.Checked = (i = index)) |> Array.forall id)
for delay in [0; 10000] do
    values.["hideTabsDelayMilliseconds"] <- box delay
    behavior.refresh()
    check (sprintf "Behavior delay boundary %d" delay) ((behaviorControls |> List.pick(function :? TextBox as t -> Some t | _ -> None)).Text = string delay)

// Audit all 22 appearance editors against their corresponding record fields.
// Reflection is test-only and avoids exposing control internals in production.
let editorField = typeof<AppearanceView>.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic) |> Array.find(fun f -> f.FieldType = typeof<Map2<string,IPropEditor>>)
let appearanceEditors = editorField.GetValue(appearanceView) :?> Map2<string,IPropEditor>
let appearanceFields = FSharpType.GetRecordFields(typeof<TabAppearanceInfo>)
let changedValues = appearanceFields |> Array.mapi(fun i field ->
    if field.PropertyType = typeof<int> then box(100+i)
    elif field.PropertyType = typeof<bool> then box false
    else box(Drawing.Color.FromArgb(255, 40+i, 80+i, 120+i)))
appearance <- FSharpValue.MakeRecord(typeof<TabAppearanceInfo>, changedValues) :?> TabAppearanceInfo
appearanceView.refresh()
let uiFields = appearanceFields |> Array.filter(fun f -> f.Name <> "tabHeightOffset")
check "Appearance audit covers all 22 editable record fields" (uiFields.Length = 22)
for field in uiFields do
    let actual = (appearanceEditors.find field.Name).value
    let expected = field.GetValue(appearance, null)
    check ("Appearance field " + field.Name) (actual = expected)

appearance <- defaults
root.["CustomColorThemes"] <- JArray.Parse("""[{"name":"External theme","inactiveTextColor":1193046}]""")
root.["SavedCustomColors"] <- JObject.Parse("""{"inactiveTextColor":6636321}""")
appearanceView.refresh()
let themeCombo = controls (appearanceView :> ISettingsView).control |> Seq.pick(function :? ComboBox as c -> Some c | _ -> None)
check "external custom theme additions refresh the combo" (themeCombo.Items.Contains("External theme"))
let savedField = typeof<AppearanceView>.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic) |> Array.find(fun f -> f.FieldType = typeof<ColorThemeData option>)
let savedColors = savedField.GetValue(appearanceView) :?> ColorThemeData option
check "saved custom colors reload from settings" (savedColors |> Option.exists(fun c -> c.inactiveTextColor = 6636321))
root.Remove("CustomColorThemes") |> ignore
root.Remove("SavedCustomColors") |> ignore
appearanceView.refresh()
check "external custom theme removal refreshes the combo" (not (themeCombo.Items.Contains("External theme")))

// All twelve hotkey fields must reload, including custom number keys hidden
// while Ctrl/Alt mode is enabled. Exercise both native and managed controls.
let auditShortcuts (view: ShortcutKeysView) label =
    let inputs = controls (view :> ISettingsView).control |> Seq.choose(function :? Bemo.Win32.HotKeyControl as c -> Some c | _ -> None) |> Seq.toArray
    let hotkeyNames = (HotKeyPolicy.numbers |> List.map HotKeyPolicy.activateTabKey) @ [HotKeyPolicy.nextTabKey; HotKeyPolicy.prevTabKey; HotKeyPolicy.newTabRightKey]
    check (label + " has twelve hotkey fields") (inputs.Length = hotkeyNames.Length)
    for index, key in hotkeyNames |> List.indexed do keys.[key] <- 0x0270 + index
    for ctrl, alt in [false,false; true,false; false,true; false,false] do
        values.[HotKeyPolicy.enableCtrlNumberSetting] <- box ctrl
        values.[HotKeyPolicy.enableAltNumberSetting] <- box alt
        view.refresh()
        let mode = HotKeyPolicy.numberKeyMode ctrl alt
        for index, key in hotkeyNames |> List.indexed do
            let expected = if index < 9 then HotKeyPolicy.numberKeyCode mode (fun n -> keys.[HotKeyPolicy.activateTabKey n]) (index+1) else keys.[key]
            check (sprintf "%s %s Ctrl=%b Alt=%b" label key ctrl alt) (inputs.[index].HotKey = expected)
auditShortcuts shortcuts "Native"
Bemo.Win32.HotKeyControl.UseManaged <- true
let managedShortcuts = ShortcutKeysView()
host.Controls.Add((managedShortcuts :> ISettingsView).control)
for control in controls (managedShortcuts :> ISettingsView).control do control.Handle |> ignore
auditShortcuts managedShortcuts "Managed"
check "the complete field audit performs no writes" (writes = baselineWrites)
// An invalid externally supplied width must not latch the change guard and
// prevent the user's next valid edit from being saved.
let pinnedEditor = appearanceEditors.find "tabPinnedTabWidth"
let mutable pinnedChanges = 0
pinnedEditor.changed.Add(fun () -> pinnedChanges <- pinnedChanges + 1)
appearance <- { appearance with tabPinnedTabWidth = 42 }
appearanceView.refresh()
check "invalid external width refresh does not write settings" (writes = baselineWrites)
let pinnedInput = controls pinnedEditor.control |> Seq.pick(function :? NumericUpDown as n -> Some n | _ -> None)
pinnedInput.Value <- 120M
check "valid user edit still fires after an invalid external width" (pinnedChanges = 1 && writes > baselineWrites)
(managedShortcuts :> ISettingsView).control.Dispose()
for view in views do view.control.Dispose()
host.Dispose()
