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
            if isStandAlone then "."
            else Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
        Path.Combine(path, "WindowTabsSettings.txt")

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
            tabOverlap = 20
            tabHeightOffset = 1
            tabIndentFlipped = 150
            tabIndentNormal = 4
            tabTextColor = Color.FromRGB(0x000000)
            tabNormalBgColor = Color.FromRGB(0x9FC4F0)
            tabHighlightBgColor = Color.FromRGB(0xBDD5F4)
            tabActiveBgColor = Color.FromRGB(0xFAFCFE)
            tabFlashBgColor = Color.FromRGB(0xFFBBBB)
            tabBorderColor = Color.FromRGB(0x3A70B1)
        }
 
    member this.darkModeTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabTextColor = Color.FromRGB(0xFFFFFF)         
            tabNormalBgColor = Color.FromRGB(0x0D0D0D)     
            tabHighlightBgColor = Color.FromRGB(0x1E1E1E)  
            tabActiveBgColor = Color.FromRGB(0x2D2D2D)     
            tabFlashBgColor = Color.FromRGB(0x772222)      
            tabBorderColor = Color.FromRGB(0x333333)       
        }

    member this.darkModeBlueTabAppearance =
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabTextColor = Color.FromRGB(0xE0E0E0)         
            tabNormalBgColor = Color.FromRGB(0x111827)    
            tabHighlightBgColor = Color.FromRGB(0x4B5970)  
            tabActiveBgColor = Color.FromRGB(0x273548)     
            tabFlashBgColor = Color.FromRGB(0x991B1B)      
            tabBorderColor = Color.FromRGB(0x374151)       
        }

    member this.update f = this.settings <- f(this.settings)

    member x.settings
        with get() =
            if cachedSettingsRec.IsNone then 
                try
                    let settingsJson = this.settingsJson
                    let settings = {
                        includedPaths = Set2(settingsJson.getStringArray("includedPaths").def(List2()))
                        excludedPaths = Set2(settingsJson.getStringArray("excludedPaths").def(List2()))
                        autoGroupingPaths = Set2(settingsJson.getStringArray("autoGroupingPaths").def(List2()))
                        licenseKey = settingsJson.getString("licenseKey").def("")
                        ticket = settingsJson.getString("ticket")
                        runAtStartup = settingsJson.getBool("runAtStartup").def(hasExistingSettings.not)
                        hideInactiveTabs = settingsJson.getBool("hideInactiveTabs").def(false)
                        enableTabbingByDefault = settingsJson.getBool("enableTabbingByDefault").def(hasExistingSettings.not)
                        enableCtrlNumberHotKey = settingsJson.getBool("enableCtrlNumberHotKey").def(false)
                        enableHoverActivate = settingsJson.getBool("enableHoverActivate").def(false)
                        makeTabsNarrowerByDefault = settingsJson.getBool("makeTabsNarrowerByDefault").def(false)
                        tabPositionByDefault = settingsJson.getString("tabPositionByDefault").def("right")
                        hideTabsWhenDownByDefault = 
                            // Handle backward compatibility: convert old bool values to new string format
                            // First try to get as string (new format)
                            match settingsJson.getString("hideTabsWhenDownByDefault") with
                            | Some(stringValue) -> stringValue
                            | None ->
                                // If not a string, try as bool (old format)
                                try
                                    match settingsJson.getBool("hideTabsWhenDownByDefault") with
                                    | Some(boolValue) -> if boolValue then "down" else "never"
                                    | None -> "never"
                                with
                                | _ -> "never"
                        hideTabsDelayMilliseconds = settingsJson.getInt32("hideTabsDelayMilliseconds").def(3000)
                        version = settingsJson.getString("version").def(String.Empty)
                        tabAppearance =
                            try
                                let appearanceObject = settingsJson.getObject("tabAppearance").def(JObject())
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
                        makeTabsNarrowerByDefault = false
                        tabPositionByDefault = "right"
                        hideTabsWhenDownByDefault = "never"
                        hideTabsDelayMilliseconds = 3000
                        version = String.Empty
                        tabAppearance = this.defaultTabAppearance
                    }
                    cachedSettingsRec <- Some(defaultSettings)
                    // Optionally log the error for debugging
                    System.Diagnostics.Debug.WriteLine(sprintf "Settings loading failed: %s" ex.Message)
            cachedSettingsRec.Value

        and set(settings) =
            let settingsJson = this.settingsJson
            settingsJson.setString("version", settings.version)
            settingsJson.setString("licenseKey", settings.licenseKey)
            settings.ticket.iter <| fun ticket -> settingsJson.setString("ticket", ticket)
            settingsJson.setBool("runAtStartup", settings.runAtStartup)
            settingsJson.setBool("hideInactiveTabs", settings.hideInactiveTabs)
            settingsJson.setBool("enableTabbingByDefault", settings.enableTabbingByDefault)
            settingsJson.setBool("enableCtrlNumberHotKey", settings.enableCtrlNumberHotKey)
            settingsJson.setBool("enableHoverActivate", settings.enableHoverActivate)
            settingsJson.setBool("makeTabsNarrowerByDefault", settings.makeTabsNarrowerByDefault)
            settingsJson.setString("tabPositionByDefault", settings.tabPositionByDefault)
            settingsJson.setString("hideTabsWhenDownByDefault", settings.hideTabsWhenDownByDefault)
            settingsJson.setInt32("hideTabsDelayMilliseconds", settings.hideTabsDelayMilliseconds)
            settingsJson.setStringArray("includedPaths", settings.includedPaths.items)
            settingsJson.setStringArray("excludedPaths", settings.excludedPaths.items)
            settingsJson.setStringArray("autoGroupingPaths", settings.autoGroupingPaths.items)
            let appearanceObject =
                let appearance = settings.tabAppearance
                let obj = JObject()
                let props = appearance.GetType().GetProperties()
                let values = FSharpValue.GetRecordFields(appearance)
                List2(Seq.zip props values).iter <| fun (prop, value) ->
                    let key = prop.Name
                    match value with
                    | :? Color as value -> obj.setString(key, sprintf "%X" (value.ToRGB()))
                    | :? int as value -> obj.setInt64(key, int64(value))
                    | :? string as value -> obj.setString(key, value)
                    | _ -> ()
                obj
            settingsJson.setObject("tabAppearance", appearanceObject)
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
