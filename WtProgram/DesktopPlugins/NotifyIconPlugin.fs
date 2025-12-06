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
                        // Update language menu checkmarks
                        let currentLanguage =
                            try
                                let settingsJson = Services.settings.root
                                let value = settingsJson.["language"]
                                if value = null then "English" else value.ToString()
                            with
                            | _ -> "English"
                        let normalizedLanguage = Localization.normalizeLanguageString(currentLanguage)

                        for j in 0 .. menuItem.MenuItems.Count - 1 do
                            let langItem = menuItem.MenuItems.[j]
                            if langItem.Text = "English" then
                                langItem.Checked <- (normalizedLanguage = "English")
                                langItem.Enabled <- not (normalizedLanguage = "English")
                            elif langItem.Text = "Japanese" then
                                langItem.Checked <- (normalizedLanguage = "Japanese")
                                langItem.Enabled <- not (normalizedLanguage = "Japanese")
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

    member this.createLanguageMenu() =
        let languageMenu = new MenuItem(Localization.getString("Language"))
        let currentLanguage =
            try
                let settingsJson = Services.settings.root
                let value = settingsJson.["language"]
                if value = null then "English" else value.ToString()
            with
            | _ -> "English"
        let normalizedLanguage = Localization.normalizeLanguageString(currentLanguage)

        let englishItem = new MenuItem("English")
        englishItem.Checked <- (normalizedLanguage = "English")
        englishItem.Enabled <- not (normalizedLanguage = "English")
        englishItem.Click.Add <| fun _ ->
            try
                let json = Services.settings.root
                json.["language"] <- JToken.FromObject("English")
                Services.settings.root <- json
                Localization.setLanguageByString("English")
                closeSettingsDialog()
                MessageBox.Show("Language has been changed to English.", "Language Change", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
            with
            | ex -> MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

        let japaneseItem = new MenuItem("Japanese")
        japaneseItem.Checked <- (normalizedLanguage = "Japanese")
        japaneseItem.Enabled <- not (normalizedLanguage = "Japanese")
        japaneseItem.Click.Add <| fun _ ->
            try
                let json = Services.settings.root
                json.["language"] <- JToken.FromObject("Japanese")
                Services.settings.root <- json
                Localization.setLanguageByString("Japanese")
                closeSettingsDialog()
                MessageBox.Show("Language has been changed to Japanese.", "Language Change", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
            with
            | ex -> MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
            
        languageMenu.MenuItems.Add(englishItem) |> ignore
        languageMenu.MenuItems.Add(japaneseItem) |> ignore
        languageMenu

    interface IPlugin with
        member this.init() =
            let notifyIcon = this.icon
            let contextMenu = notifyIcon.ContextMenu

            // Create menu items
            let settingsMenuItem = new MenuItem(Localization.getString("Settings"))
            settingsMenuItem.Click.Add <| fun _ -> Services.managerView.show()
            settingsMenuItem.Tag <- box("Settings")
            this.contextMenuItems.Add(settingsMenuItem) |> ignore

            let languageMenu = this.createLanguageMenu()
            languageMenu.Tag <- box("Language")
            this.contextMenuItems.Add(languageMenu) |> ignore

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
