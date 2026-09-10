// Run with .NET Framework fsi.exe after building SettingsRefreshValidation.
// This test uses in-memory services, never displays windows, and never saves files.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../Newtonsoft.Json.dll"
#r "../WtProgram/bin/Debug/SettingsRefreshValidation/WindowTabs.exe"
#r "../WtProgram/bin/Debug/SettingsRefreshValidation/Win32.dll"
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
        if field.PropertyType = typeof<int> then box 25
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
for view in views do view.control.Dispose()
host.Dispose()
