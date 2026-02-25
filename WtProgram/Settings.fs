namespace Bemo
open System
open System.Drawing
open System.Collections.Generic
open System.IO
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open Newtonsoft.Json.Linq
 
type Settings(isStandAlone) as this =
    let mutable cachedSettingsString = None
    let mutable cachedSettingsRec = None
    let mutable hasExistingSettings = false
    let settingChangedEvent = Event<string* obj>()
    let valueCache = Dictionary<string, obj>()

    do
        hasExistingSettings <- this.fileExists
        Services.register(this :> ISettings)

    member this.clearCaches() =
        cachedSettingsString <- None
        cachedSettingsRec <- None
        valueCache.Clear()

    member this.path =
        let path = 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
        Path.Combine(path, "WindowTabsSettings.txt")
    // // debug: use local settings file
    // member this.path =
    //     let path = 
    //         if isStandAlone then "."
    //         else Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
    //     Path.Combine(path, "WindowTabsSettings.txt")

    member this.fileExists : bool = File.Exists(this.path) 

    member this.settingsString
        with get() = 
            if cachedSettingsString.IsNone then 
                cachedSettingsString <- 
                    try
                        if this.fileExists then Some(File.ReadAllText(this.path)) else None
                    with
                    | _ -> None  // Return None if file reading fails
            cachedSettingsString

        and set(newSettings : string option) =
            try
                let settingsDir = Path.GetDirectoryName(this.path)
                if Directory.Exists(settingsDir).not then
                    Directory.CreateDirectory(settingsDir).ignore
                File.WriteAllText(this.path, newSettings.Value)
                this.clearCaches()
            with
            | ex ->
                // Log error but don't crash
                System.Diagnostics.Debug.WriteLine(sprintf "Failed to save settings: %s" ex.Message)
            
    member this.settingsJson
        with get() =
            try
                this.settingsString.map(fun s ->
                    try
                        parseJsoncObject(s)
                    with
                    | _ -> JObject()  // Return empty JObject if parsing fails
                ).def(JObject())
            with
            | _ -> JObject()  // Return empty JObject if any error occurs
        and set(settingsJson:JObject) = this.settingsString <- Some(settingsJson.ToString())

    member this.defaultTabAppearance =
        {
            tabHeight = 25
            tabMaxWidth = 200
            tabPinnedTabWidth = 0
            tabOverlap = 20
            tabHeightOffset = 1
            tabIndentFlipped = 150
            tabIndentNormal = 4
            tabInactiveTextColor = Color.FromRGB(0x000000)
            tabMouseOverTextColor = Color.FromRGB(0x000000)
            tabActiveTextColor = Color.FromRGB(0x000000)
            tabFlashTextColor = Color.FromRGB(0x000000)
            tabInactiveTabColor = Color.FromRGB(0x9FC4F0)
            tabMouseOverTabColor = Color.FromRGB(0xBDD5F4)
            tabActiveTabColor = Color.FromRGB(0xFAFCFE)
            tabFlashTabColor = Color.FromRGB(0xFFBBBB)
            tabInactiveBorderColor = Color.FromRGB(0x3A70B1)
            tabMouseOverBorderColor = Color.FromRGB(0x3A70B1)
            tabActiveBorderColor = Color.FromRGB(0x3A70B1)
            tabFlashBorderColor = Color.FromRGB(0x3A70B1)
        }

    member this.darkModeTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0xFFFFFF)
            tabActiveTextColor = Color.FromRGB(0xFFFFFF)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = Color.FromRGB(0x0D0D0D)
            tabMouseOverTabColor = Color.FromRGB(0x1E1E1E)
            tabActiveTabColor = Color.FromRGB(0x2D2D2D)
            tabFlashTabColor = Color.FromRGB(0x772222)
            tabInactiveBorderColor = Color.FromRGB(0x333333)
            tabMouseOverBorderColor = Color.FromRGB(0x333333)
            tabActiveBorderColor = Color.FromRGB(0x333333)
            tabFlashBorderColor = Color.FromRGB(0x333333)
        }

    member this.darkModeBlueTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xE0E0E0)
            tabMouseOverTextColor = Color.FromRGB(0xE0E0E0)
            tabActiveTextColor = Color.FromRGB(0xE0E0E0)
            tabFlashTextColor = Color.FromRGB(0xE0E0E0)
            tabInactiveTabColor = Color.FromRGB(0x111827)
            tabMouseOverTabColor = Color.FromRGB(0x4B5970)
            tabActiveTabColor = Color.FromRGB(0x273548)
            tabFlashTabColor = Color.FromRGB(0x991B1B)
            tabInactiveBorderColor = Color.FromRGB(0x374151)
            tabMouseOverBorderColor = Color.FromRGB(0x374151)
            tabActiveBorderColor = Color.FromRGB(0x374151)
            tabFlashBorderColor = Color.FromRGB(0x374151)
        }

    member this.lightMonoTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0x000000)
            tabMouseOverTextColor = Color.FromRGB(0x000000)
            tabActiveTextColor = Color.FromRGB(0x000000)
            tabFlashTextColor = Color.FromRGB(0x000000)
            tabInactiveTabColor = Color.FromRGB(0xA0A0A0)
            tabMouseOverTabColor = Color.FromRGB(0xD0D0D0)
            tabActiveTabColor = Color.FromRGB(0xFFFFFF)
            tabFlashTabColor = Color.FromRGB(0xD4D4D4)
            tabInactiveBorderColor = Color.FromRGB(0x252525)
            tabMouseOverBorderColor = Color.FromRGB(0x252525)
            tabActiveBorderColor = Color.FromRGB(0x252525)
            tabFlashBorderColor = Color.FromRGB(0x252525)
        }

    member this.darkMonoTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0x111111)
            tabActiveTextColor = Color.FromRGB(0xFFFFFF)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = Color.FromRGB(0x0D0D0D)
            tabMouseOverTabColor = Color.FromRGB(0xDDDDDD)
            tabActiveTabColor = Color.FromRGB(0x616161)
            tabFlashTabColor = Color.FromRGB(0x808080)
            tabInactiveBorderColor = Color.FromRGB(0x787878)
            tabMouseOverBorderColor = Color.FromRGB(0xF2F2F2)
            tabActiveBorderColor = Color.FromRGB(0x6B6B6B)
            tabFlashBorderColor = Color.FromRGB(0x787878)
        }

    member this.darkRedFrameTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0x111111)
            tabActiveTextColor = Color.FromRGB(0xB13A3A)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = Color.FromRGB(0x0D0D0D)
            tabMouseOverTabColor = Color.FromRGB(0xB13A3A)
            tabActiveTabColor = Color.FromRGB(0x250A0B)
            tabFlashTabColor = Color.FromRGB(0x808080)
            tabInactiveBorderColor = Color.FromRGB(0xB13A3A)
            tabMouseOverBorderColor = Color.FromRGB(0xFF6666)
            tabActiveBorderColor = Color.FromRGB(0xCC4444)
            tabFlashBorderColor = Color.FromRGB(0xB13A3A)
        }

    member this.update f = this.settings <- f(this.settings)

    member x.settings
        with get() =
            if cachedSettingsRec.IsNone then 
                try
                    let settingsJson = this.settingsJson
                    let settings = {
                        includedPaths = Set2(settingsJson.getStringArray("IncludedPaths").def(List2()))
                        excludedPaths = Set2(settingsJson.getStringArray("ExcludedPaths").def(List2()))
                        autoGroupingPaths = Set2(settingsJson.getStringArray("AutoGroupingPaths").def(List2()))
                        licenseKey = settingsJson.getString("LicenseKey").def("")
                        ticket = settingsJson.getString("Ticket")
                        runAtStartup = settingsJson.getBool("RunAtStartup").def(hasExistingSettings.not)
                        hideInactiveTabs = settingsJson.getBool("HideInactiveTabs").def(false)
                        enableTabbingByDefault = settingsJson.getBool("EnableTabbingByDefault").def(hasExistingSettings.not)
                        enableCtrlNumberHotKey = settingsJson.getBool("EnableCtrlNumberHotKey").def(false)
                        enableHoverActivate = settingsJson.getBool("EnableHoverActivate").def(false)
                        tabPositionByDefault =
                            // Handle backward compatibility: convert old format to new TopXxx format
                            match settingsJson.getString("TabPositionByDefault") with
                            | Some("left") -> "TopLeft"
                            | Some("center") -> "TopCenter"
                            | Some("right") -> "TopRight"
                            | Some(v) -> v  // Already in TopXxx format or other valid value
                            | None -> "TopRight"
                        hideTabsWhenDownByDefault =
                            // Handle backward compatibility: convert old bool values to new string format
                            // First try to get as string (new format)
                            match settingsJson.getString("HideTabsWhenDownByDefault") with
                            | Some(stringValue) -> stringValue
                            | None ->
                                // If not a string, try as bool (old format)
                                try
                                    match settingsJson.getBool("HideTabsWhenDownByDefault") with
                                    | Some(boolValue) -> if boolValue then "down" else "never"
                                    | None -> "never"
                                with
                                | _ -> "never"
                        hideTabsDelayMilliseconds = settingsJson.getInt32("HideTabsDelayMilliseconds").def(3000)
                        hideTabsOnFullscreen = settingsJson.getBool("HideTabsOnFullscreen").def(true)
                        snapTabHeightMargin = settingsJson.getBool("SnapTabHeightMargin").def(false)
                        version = settingsJson.getString("Version").def(String.Empty)
                        tabAppearance =
                            try
                                let appearanceObject = settingsJson.getObject("TabAppearance").def(JObject())
                                appearanceObject.items.fold this.defaultTabAppearance <| fun appearance (key,value) ->
                                    try
                                        let value = 
                                            let value = (value :?> JValue).Value
                                            let fieldType = Serialize.getFieldType (appearance.GetType()) key
                                            if fieldType = typeof<Int32> then box(unbox<Int64>(value).Int32)
                                            elif fieldType = typeof<Color> then box(Color.FromRGB(Int32.Parse(unbox<string>(value), Globalization.NumberStyles.HexNumber)))
                                            else failwith "UNKNOWN TYPE"
                                        Serialize.writeField appearance key value :?> TabAppearanceInfo
                                    with
                                    | _ -> appearance  // Skip invalid field and keep current value
                            with
                            | _ -> this.defaultTabAppearance  // Use default appearance if parsing fails
                    }
                    cachedSettingsRec <- Some(settings)
                with
                | ex ->
                    // If settings loading completely fails, use all defaults
                    let defaultSettings = {
                        includedPaths = Set2(List2())
                        excludedPaths = Set2(List2())
                        autoGroupingPaths = Set2(List2())
                        licenseKey = ""
                        ticket = None
                        runAtStartup = false
                        hideInactiveTabs = false
                        enableTabbingByDefault = true
                        enableCtrlNumberHotKey = false
                        enableHoverActivate = false
                        tabPositionByDefault = "TopRight"
                        hideTabsWhenDownByDefault = "never"
                        hideTabsDelayMilliseconds = 3000
                        hideTabsOnFullscreen = true
                        snapTabHeightMargin = false
                        version = String.Empty
                        tabAppearance = this.defaultTabAppearance
                    }
                    cachedSettingsRec <- Some(defaultSettings)
                    // Optionally log the error for debugging
                    System.Diagnostics.Debug.WriteLine(sprintf "Settings loading failed: %s" ex.Message)
            cachedSettingsRec.Value

        and set(settings) =
            let settingsJson = this.settingsJson
            settingsJson.setString("Version", settings.version)
            settingsJson.setString("LicenseKey", settings.licenseKey)
            settings.ticket.iter <| fun ticket -> settingsJson.setString("Ticket", ticket)
            settingsJson.setBool("RunAtStartup", settings.runAtStartup)
            settingsJson.setBool("HideInactiveTabs", settings.hideInactiveTabs)
            settingsJson.setBool("EnableTabbingByDefault", settings.enableTabbingByDefault)
            settingsJson.setBool("EnableCtrlNumberHotKey", settings.enableCtrlNumberHotKey)
            settingsJson.setBool("EnableHoverActivate", settings.enableHoverActivate)
            settingsJson.setString("TabPositionByDefault", settings.tabPositionByDefault)
            settingsJson.setString("HideTabsWhenDownByDefault", settings.hideTabsWhenDownByDefault)
            settingsJson.setInt32("HideTabsDelayMilliseconds", settings.hideTabsDelayMilliseconds)
            settingsJson.setBool("HideTabsOnFullscreen", settings.hideTabsOnFullscreen)
            settingsJson.setBool("SnapTabHeightMargin", settings.snapTabHeightMargin)
            settingsJson.setStringArray("IncludedPaths", settings.includedPaths.items)
            settingsJson.setStringArray("ExcludedPaths", settings.excludedPaths.items)
            settingsJson.setStringArray("AutoGroupingPaths", settings.autoGroupingPaths.items)
            let appearanceObject =
                let appearance = settings.tabAppearance
                let obj = JObject()
                // Use FSharpType.GetRecordFields to get properties in definition order
                // This matches the order of FSharpValue.GetRecordFields values
                let props = FSharpType.GetRecordFields(appearance.GetType())
                let values = FSharpValue.GetRecordFields(appearance)
                List2(Seq.zip props values).iter <| fun (prop, value) ->
                    let key = prop.Name
                    match value with
                    | :? Color as value -> obj.setString(key, sprintf "%X" (value.ToRGB()))
                    | :? int as value -> obj.setInt64(key, int64(value))
                    | :? string as value -> obj.setString(key, value)
                    | _ -> ()
                obj
            settingsJson.setObject("TabAppearance", appearanceObject)
            this.settingsJson <- settingsJson

    interface ISettings with

        member x.setValue((key,value)) =
            valueCache.Remove(key).ignore
            let settings = x.settings
            let settings = Serialize.writeField settings key value
            x.settings <- unbox<SettingsRec>(settings)
            settingChangedEvent.Trigger(key, value)

        member x.getValue(key) = 
            match valueCache.GetValue(key) with
            | None ->
                let settings = x.settings
                let value = Serialize.readField settings key
                valueCache.Add(key, value)
                value
            | Some(value) -> value

        member x.notifyValue key f =
            settingChangedEvent.Publish.Add <| fun(changedKey, value) ->
                if changedKey = key then f(value)

        member x.root
            with get() = this.settingsJson
            and set(value) = this.settingsJson <- value 
