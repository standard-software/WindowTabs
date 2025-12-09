namespace Bemo
open System
open System.Windows.Forms
open System.Reflection
open Newtonsoft.Json.Linq
open System.Diagnostics
open System.IO
open System.Threading

type NotifyIconPlugin() as this =
    let Cell = CellScope()

    let closeSettingsDialog() =
        // Try to close any existing settings dialog using mutex check
        let mutable tempMutex : Mutex option = None
        let mutexCreated = ref false
        try
            tempMutex <- Some(new Mutex(true, "WindowTabsSettingsDialog", mutexCreated))
            if not !mutexCreated then
                // Dialog exists in another process, we can't close it directly
                // but we'll clear our local reference
                ()
            // Always release the mutex immediately
            match tempMutex with
            | Some m ->
                try m.ReleaseMutex(); m.Dispose() with _ -> ()
            | None -> ()
        with _ -> ()
        // Close local form reference if exists
        match DesktopManagerFormState.currentForm with
        | Some form ->
            try form.Close() with _ -> ()
        | None -> ()

    member this.icon = Cell.cacheProp this <| fun() ->
        let notifyIcon = new NotifyIcon()
        notifyIcon.Visible <- true
        notifyIcon.Text <- "WindowTabs (version " + Services.program.version + ")"
        notifyIcon.Icon <- Services.openIcon("Bemo.ico")
        let contextMenu = new ContextMenu()

        // Apply dark mode setting and update menu texts when menu is about to be shown
        contextMenu.Popup.Add <| fun _ ->
            let darkModeEnabled =
                try
                    let json = Services.settings.root
                    match json.getBool("enableMenuDarkMode") with
                    | Some(value) -> value
                    | None -> false
                with | _ -> false
            DarkMode.setDarkModeForMenus(darkModeEnabled)

            // Update all menu item texts by checking their Tags
            for i in 0 .. contextMenu.MenuItems.Count - 1 do
                let menuItem = contextMenu.MenuItems.[i]
                match menuItem.Tag with
                | :? string as tag ->
                    match tag with
                    | "Settings" -> menuItem.Text <- Localization.getString("Settings")
                    | "Language" ->
                        menuItem.Text <- Localization.getString("Language")
                        // Update language menu checkmarks using current language from Localization module
                        let currentLanguage = Localization.currentLanguage

                        for j in 0 .. menuItem.MenuItems.Count - 1 do
                            let langItem = menuItem.MenuItems.[j]
                            // Get language name from Tag (stored without .json extension)
                            match langItem.Tag with
                            | :? string as langName ->
                                langItem.Checked <- (currentLanguage = langName)
                                langItem.Enabled <- not (currentLanguage = langName)
                            | _ -> ()
                    | "Disable" ->
                        menuItem.Text <- Localization.getString("Disable")
                        // Update checkbox state
                        menuItem.Checked <- Services.program.isDisabled
                    | "RestartWindowTabs" -> menuItem.Text <- Localization.getString("RestartWindowTabs")
                    | "CloseWindowTabs" -> menuItem.Text <- Localization.getString("CloseWindowTabs")
                    | _ -> ()
                | _ -> ()

            // Update Settings menu item enabled state based on disabled status
            for i in 0 .. contextMenu.MenuItems.Count - 1 do
                let menuItem = contextMenu.MenuItems.[i]
                match menuItem.Tag with
                | :? string as tag when tag = "Settings" ->
                    menuItem.Enabled <- not Services.program.isDisabled
                | _ -> ()

        notifyIcon.ContextMenu <- contextMenu
        notifyIcon.DoubleClick.Add <| fun _ -> Services.managerView.show()
        notifyIcon

    member this.contextMenuItems = this.icon.ContextMenu.MenuItems

    member this.addItem(text, handler) =
        this.contextMenuItems.Add(text, EventHandler(fun obj (e:EventArgs) -> handler())) |> ignore

    member this.onNewVersion() =
        this.icon.ShowBalloonTip(
            1000,
            "A new version is available.",
            "Please visit windowtabs.com to download the latest version.",
            ToolTipIcon.Info
        )

    member this.restartApplication() =
        let exePath = Assembly.GetExecutingAssembly().Location
        // Start new process with a delay using cmd
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "cmd.exe"
        startInfo.Arguments <- sprintf "/c timeout /t 3 /nobreak >nul && start \"\" \"%s\"" exePath
        startInfo.WindowStyle <- ProcessWindowStyle.Hidden
        startInfo.CreateNoWindow <- true
        try
            Process.Start(startInfo) |> ignore
            Services.program.shutdown()
        with
        | ex -> MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

    member this.getLanguageFolder() =
        let exePath = Assembly.GetExecutingAssembly().Location
        let exeDir = Path.GetDirectoryName(exePath)
        Path.Combine(exeDir, "Language")

    // Returns list of (displayName, fileName) tuples (supports JSONC format with comments)
    member this.getLanguageListFromFileList() : (string * string) list =
        try
            let fileListPath = Path.Combine(this.getLanguageFolder(), "FileList.json")
            if File.Exists(fileListPath) then
                let rawJson = File.ReadAllText(fileListPath)
                let arr = parseJsoncArray(rawJson)
                arr
                |> Seq.map (fun t ->
                    let obj = t :?> JObject
                    let name = obj.["name"].ToString()
                    let fileName = obj.["fileName"].ToString().Replace(".json", "")
                    (name, fileName))
                |> Seq.toList
            else
                // FileList.json not found - return empty list
                []
        with
        | _ -> []

    member this.createLanguageMenu() =
        // Load language list from FileList.json
        let languages = this.getLanguageListFromFileList()

        // If language list is empty, return None (hide Language menu)
        if languages.IsEmpty then
            None
        else
            let languageMenu = new MenuItem(Localization.getString("Language"))
            let currentLanguage = Localization.currentLanguage

            for (displayName, fileName) in languages do
                let langItem = new MenuItem(displayName)
                langItem.Checked <- (currentLanguage = fileName)
                langItem.Enabled <- not (currentLanguage = fileName)
                langItem.Tag <- box(fileName)  // Store fileName (without .json) in Tag for language switching
                langItem.Click.Add <| fun _ ->
                    try
                        let json = Services.settings.root
                        json.["language"] <- JToken.FromObject(fileName)
                        Services.settings.root <- json
                        Localization.setLanguage(fileName)
                        closeSettingsDialog()
                        MessageBox.Show(sprintf "Language has been changed to %s." displayName, "Language Change", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
                    with
                    | ex -> MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                languageMenu.MenuItems.Add(langItem) |> ignore

            Some(languageMenu)

    interface IPlugin with
        member this.init() =
            let notifyIcon = this.icon
            let contextMenu = notifyIcon.ContextMenu

            // Create menu items
            let settingsMenuItem = new MenuItem(Localization.getString("Settings"))
            settingsMenuItem.Click.Add <| fun _ -> Services.managerView.show()
            settingsMenuItem.Tag <- box("Settings")
            this.contextMenuItems.Add(settingsMenuItem) |> ignore

            // Only add Language menu if FileList.json exists and is not empty
            match this.createLanguageMenu() with
            | Some(languageMenu) ->
                languageMenu.Tag <- box("Language")
                this.contextMenuItems.Add(languageMenu) |> ignore
            | None -> ()

            //this.addItem(Localization.getString("Feedback"), Forms.openFeedback) // 404 Not Found.
            this.contextMenuItems.Add("-") |> ignore

            let disableMenuItem = new MenuItem(Localization.getString("Disable"))
            disableMenuItem.Click.Add <| fun _ ->
                let newState = not Services.program.isDisabled
                Services.program.setDisabled(newState)
            disableMenuItem.Tag <- box("Disable")
            this.contextMenuItems.Add(disableMenuItem) |> ignore

            this.contextMenuItems.Add("-") |> ignore

            let restartMenuItem = new MenuItem(Localization.getString("RestartWindowTabs"))
            restartMenuItem.Click.Add <| fun _ -> this.restartApplication()
            restartMenuItem.Tag <- box("RestartWindowTabs")
            this.contextMenuItems.Add(restartMenuItem) |> ignore

            let closeMenuItem = new MenuItem(Localization.getString("CloseWindowTabs"))
            closeMenuItem.Click.Add <| fun _ -> Services.program.shutdown()
            closeMenuItem.Tag <- box("CloseWindowTabs")
            this.contextMenuItems.Add(closeMenuItem) |> ignore

            Services.program.newVersion.Add this.onNewVersion

    interface IDisposable with
        member this.Dispose() = this.icon.Dispose()
