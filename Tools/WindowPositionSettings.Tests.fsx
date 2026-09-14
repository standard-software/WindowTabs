// Run with .NET Framework fsi.exe after a normal Debug build; see SettingsTests.md.
// This test uses in-memory services, never displays windows, and never saves files.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../Newtonsoft.Json.dll"
#r "../WtProgram/bin/Debug/FSharp.PowerPack.dll"
#r "../WtProgram/bin/Debug/Aga.Controls.dll"
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
             "hideTabsOnFullscreen"; "hideTabsWhileMoving"; "lockWindowPosition"; "snapTabHeightMargin"; "snapOnDragDetach";
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
let behavior = HotKeyView()
let table = (behavior :> ISettingsView).control :?> TableLayoutPanel
let checkbox = table.GetControlFromPosition(1, 11) :?> CheckBox
check "lock position checkbox defaults to OFF" (not checkbox.Checked)
check "behavior has thirteen rows (lock position at 11, detach snap at 12)" (table.RowCount = 13)
let beforeRefresh = writes
values.["lockWindowPosition"] <- box true
behavior.refresh()
check "refresh displays externally enabled setting" checkbox.Checked
check "refresh does not write settings" (writes = beforeRefresh)
values.["lockWindowPosition"] <- box false
behavior.refresh()
check "refresh displays externally disabled setting" (not checkbox.Checked)
check "OFF refresh does not write settings" (writes = beforeRefresh)
checkbox.Checked <- true
check "checking persists ON" (unbox<bool> values.["lockWindowPosition"] && writes = beforeRefresh + 1)
checkbox.Checked <- false
check "unchecking persists OFF" (not (unbox<bool> values.["lockWindowPosition"]) && writes = beforeRefresh + 2)
let radioRow = table.RowStyles.[7]
check "radio group keeps its original autosize row" (radioRow.SizeType = SizeType.AutoSize)
(behavior :> ISettingsView).control.Dispose()
printfn "All 9 checks passed"
